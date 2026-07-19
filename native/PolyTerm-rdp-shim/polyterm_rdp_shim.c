/* PolyTerm RDP shim implementation. See polyterm_rdp_shim.h for the ABI contract. */

/* winsock2.h must be included before windows.h ends up pulled in transitively (via the FreeRDP/WinPR
   headers below) - the classic winsock.h-vs-winsock2.h ordering pitfall - so this comes first, ahead
   of everything else in the file. */
#ifdef _WIN32
#include <winsock2.h>
#endif

#include "polyterm_rdp_shim.h"

#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <freerdp/freerdp.h>
#include <freerdp/client.h>
#include <freerdp/error.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/input.h>
#include <freerdp/settings.h>

typedef struct
{
	rdpContext parent; /* must be first member - FreeRDP casts rdpContext* <-> our type */

	prdp_framebuffer_cb on_framebuffer;
	prdp_state_cb on_state;
	void* user_data;

	/* WinPR's own CreateThread/WaitForSingleObject, not pthread directly - WinPR already provides
	   these as genuine cross-platform primitives (a thin wrapper on Windows, a pthread-based
	   emulation on Linux/macOS), so this one thread stays a single code path on every platform
	   instead of needing an #ifdef _WIN32 branch and a separate Windows pthread port. */
	HANDLE thread;
	int thread_started;
} prdpContext;

struct prdp_handle
{
	rdpContext* context;
};

/* Resets the accumulated invalid region at the start of each new frame, matching the exact pattern
   FreeRDP's own reference sample client uses (client/Sample/tf_freerdp.c's tf_begin_paint) - without
   this the region in prdp_end_paint below would just keep growing across frames instead of
   reflecting only what changed since the last one. */
static BOOL prdp_begin_paint(rdpContext* context)
{
	rdpGdi* gdi = context->gdi;
	if (gdi != NULL && gdi->primary != NULL && gdi->primary->hdc != NULL &&
	    gdi->primary->hdc->hwnd != NULL && gdi->primary->hdc->hwnd->invalid != NULL)
	{
		gdi->primary->hdc->hwnd->invalid->null = TRUE;
	}
	return TRUE;
}

static BOOL prdp_end_paint(rdpContext* context)
{
	prdpContext* ctx = (prdpContext*)context;
	rdpGdi* gdi = context->gdi;

	if (gdi == NULL || ctx->on_framebuffer == NULL)
		return TRUE;

	HGDI_WND hwnd = (gdi->primary != NULL && gdi->primary->hdc != NULL) ? gdi->primary->hdc->hwnd : NULL;
	if (hwnd == NULL || hwnd->invalid == NULL || hwnd->invalid->null)
		return TRUE; /* nothing actually changed this frame - matches the reference client's own check */

	/* FreeRDP's own accumulated bounding rect of everything that changed since the last
	   prdp_begin_paint - individual sub-rects (hwnd->cinvalid/ninvalid) exist too, but a single
	   bounding rect keeps this shim's ABI simple, and real UI changes are usually already close to
	   one contiguous area (a window, a menu, a line of text) rather than scattered pixels. */
	int32_t x = hwnd->invalid->x;
	int32_t y = hwnd->invalid->y;
	int32_t w = hwnd->invalid->w;
	int32_t h = hwnd->invalid->h;

	/* Defensive clamp to the real buffer bounds - shouldn't ever be needed given FreeRDP's own
	   invalidation only ever covers the desktop it itself owns, but a malformed/out-of-range region
	   here would otherwise read past the end of primary_buffer. */
	if (x < 0) { w += x; x = 0; }
	if (y < 0) { h += y; y = 0; }
	if (x + w > gdi->width) w = gdi->width - x;
	if (y + h > gdi->height) h = gdi->height - y;
	if (w <= 0 || h <= 0)
		return TRUE;

	const uint8_t* region_start = gdi->primary_buffer + (size_t)y * gdi->stride + (size_t)x * 4;
	ctx->on_framebuffer(ctx->user_data, region_start, gdi->width, gdi->height, x, y, w, h,
	                    (int32_t)gdi->stride);
	return TRUE;
}

static DWORD prdp_verify_certificate(freerdp* instance, const char* host, UINT16 port,
                                      const char* common_name, const char* subject,
                                      const char* issuer, const char* fingerprint, DWORD flags)
{
	(void)instance;
	(void)host;
	(void)port;
	(void)common_name;
	(void)subject;
	(void)issuer;
	(void)fingerprint;
	(void)flags;
	/* v1: accept for this session only - no persisted host-key trust store yet (unlike the SSH
	   module's TofuHostKeyPolicy). Revisit once this graduates past a first proof-of-concept. */
	return 2;
}

static BOOL prdp_pre_connect(freerdp* instance)
{
	rdpSettings* settings = instance->context->settings;
	return freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 32);
}

static BOOL prdp_post_connect(freerdp* instance)
{
	/* RGBA32 (not BGRA32) specifically so this matches IFramebufferChannel.Format's Rgba32
	   contract byte-for-byte on the C# side, same convention VncFramebufferChannel already uses -
	   letting FreeRDP's own GDI layer do this conversion means the C# side needs zero per-pixel
	   byte-swapping. */
	if (!gdi_init(instance, PIXEL_FORMAT_RGBA32))
		return FALSE;

	instance->context->update->BeginPaint = prdp_begin_paint;
	instance->context->update->EndPaint = prdp_end_paint;
	return TRUE;
}

static void prdp_post_disconnect(freerdp* instance)
{
	if (instance->context->gdi != NULL)
	{
		gdi_free(instance);
	}
}

static BOOL prdp_client_new(freerdp* instance, rdpContext* context)
{
	(void)instance;
	prdpContext* ctx = (prdpContext*)context;
	ctx->on_framebuffer = NULL;
	ctx->on_state = NULL;
	ctx->user_data = NULL;
	ctx->thread_started = 0;
	instance->PreConnect = prdp_pre_connect;
	instance->PostConnect = prdp_post_connect;
	instance->PostDisconnect = prdp_post_disconnect;
	instance->VerifyCertificateEx = prdp_verify_certificate;
	return TRUE;
}

static void prdp_client_free(freerdp* instance, rdpContext* context)
{
	(void)instance;
	(void)context;
}

static DWORD prdp_thread_proc(LPVOID arg)
{
	rdpContext* context = (rdpContext*)arg;
	prdpContext* ctx = (prdpContext*)context;
	freerdp* instance = context->instance;

	BOOL connected = freerdp_connect(instance);
	if (ctx->on_state != NULL)
	{
		if (connected)
		{
			ctx->on_state(ctx->user_data, 1, NULL);
		}
		else
		{
			/* freerdp_get_last_error_string returns a literal from FreeRDP's own static string
			   table (not heap-allocated), so it's safe to reference straight into the snprintf
			   buffer without an extra copy/lifetime concern. Includes the raw code alongside the
			   string because the string table doesn't cover every possible code (falls back to
			   "Unknown error"), and the code alone is enough to look up in error.h even then. */
			UINT32 error_code = freerdp_get_last_error(context);
			char message[256];
			snprintf(message, sizeof(message), "freerdp_connect failed: %s (0x%08" PRIX32 ")",
			         freerdp_get_last_error_string(error_code), error_code);
			ctx->on_state(ctx->user_data, 0, message);
		}
	}

	if (connected)
	{
		while (!freerdp_shall_disconnect_context(context))
		{
			HANDLE handles[64];
			DWORD count = freerdp_get_event_handles(context, handles, 64);
			if (count == 0)
				break;
			WaitForMultipleObjects(count, handles, FALSE, 100);
			if (!freerdp_check_event_handles(context))
				break;
		}
		freerdp_disconnect(instance);
	}

	if (ctx->on_state != NULL)
		ctx->on_state(ctx->user_data, 0, NULL);

	return 0;
}

prdp_handle* prdp_connect(const char* host, int32_t port, const char* username,
                          const char* password, const char* domain, int32_t width, int32_t height,
                          prdp_framebuffer_cb on_framebuffer, prdp_state_cb on_state,
                          void* user_data)
{
#ifdef _WIN32
	/* Unlike Linux/macOS (plain BSD sockets, no init step), Windows requires Winsock to be
	   initialized via WSAStartup before ANY socket call - including getaddrinfo - will work at all.
	   WinPR itself never calls this on Windows (confirmed: grepping the FreeRDP/WinPR source, the
	   only real call site is inside the reference wfreerdp.exe CLI client's own main(), not any
	   library code this shim links against) - it's entirely the embedding application's
	   responsibility. Without it, freerdp_connect fails immediately with
	   FREERDP_ERROR_DNS_NAME_NOT_FOUND even for a bare IP literal, before any real network I/O is
	   attempted - confirmed the hard way against a real Windows build. Safe to call once per
	   prdp_connect: per Microsoft's own docs, WSAStartup is reference-counted internally, so repeat
	   calls across multiple connects in this process's lifetime are expected and safe, paired 1:1
	   with the WSACleanup in prdp_free below. */
	WSADATA wsa_data;
	if (WSAStartup(MAKEWORD(2, 2), &wsa_data) != 0)
		return NULL;
#endif

	RDP_CLIENT_ENTRY_POINTS entry = { 0 };
	entry.Size = sizeof(RDP_CLIENT_ENTRY_POINTS);
	entry.Version = RDP_CLIENT_INTERFACE_VERSION;
	entry.ContextSize = sizeof(prdpContext);
	entry.ClientNew = prdp_client_new;
	entry.ClientFree = prdp_client_free;

	rdpContext* context = freerdp_client_context_new(&entry);
	if (context == NULL)
		return NULL;

	prdpContext* ctx = (prdpContext*)context;
	ctx->on_framebuffer = on_framebuffer;
	ctx->on_state = on_state;
	ctx->user_data = user_data;

	rdpSettings* settings = context->settings;
	BOOL ok = freerdp_settings_set_string(settings, FreeRDP_ServerHostname, host) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, (UINT32)port) &&
	          freerdp_settings_set_string(settings, FreeRDP_Username, username) &&
	          freerdp_settings_set_string(settings, FreeRDP_Password, password) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, (UINT32)width) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, (UINT32)height) &&
	          freerdp_settings_set_bool(settings, FreeRDP_AutoReconnectionEnabled, FALSE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, TRUE) &&
	          /* NLA (CredSSP) is required by this server (confirmed: disabling it server-side
	             rejects with HYBRID_REQUIRED_BY_SERVER), so it stays on. But WinPR's SSPI Negotiate
	             layer tries Kerberos before NTLM and, on this Ubuntu-packaged FreeRDP build, a
	             Kerberos credential-acquisition failure ("Cannot find KDC for realm") aborts the
	             whole handshake instead of falling back to NTLM - confirmed independently of this
	             shim's own code, via the distro's own xfreerdp3 CLI failing identically against the
	             same real server/credential. Excluding kerberos from the offered package list (the
	             same fix as xfreerdp3's own `/auth-pkg-list:!kerberos` flag) skips straight to NTLM,
	             which then completes the full handshake correctly. Only relevant for non-domain/
	             workgroup targets - a real Kerberos realm wouldn't need this. */
	          freerdp_settings_set_string(settings, FreeRDP_AuthenticationPackageList, "!kerberos");

	if (domain != NULL && domain[0] != '\0')
		ok = ok && freerdp_settings_set_string(settings, FreeRDP_Domain, domain);

	if (!ok)
	{
		freerdp_client_context_free(context);
		return NULL;
	}

	prdp_handle* handle = (prdp_handle*)malloc(sizeof(prdp_handle));
	if (handle == NULL)
	{
		freerdp_client_context_free(context);
		return NULL;
	}
	handle->context = context;

	ctx->thread = CreateThread(NULL, 0, prdp_thread_proc, context, 0, NULL);
	if (ctx->thread == NULL)
	{
		free(handle);
		freerdp_client_context_free(context);
		return NULL;
	}
	ctx->thread_started = 1;

	return handle;
}

void prdp_send_mouse(prdp_handle* handle, uint16_t flags, int32_t x, int32_t y)
{
	if (handle == NULL || handle->context == NULL)
		return;
	freerdp_input_send_mouse_event(handle->context->input, flags, (UINT16)x, (UINT16)y);
}

void prdp_send_key(prdp_handle* handle, int32_t down, uint32_t rdp_scancode)
{
	if (handle == NULL || handle->context == NULL)
		return;
	freerdp_input_send_keyboard_event_ex(handle->context->input, down != 0, FALSE, rdp_scancode);
}

void prdp_send_unicode_key(prdp_handle* handle, int32_t down, uint16_t unicode_code_point)
{
	if (handle == NULL || handle->context == NULL)
		return;
	UINT16 flags = down ? 0 : KBD_FLAGS_RELEASE;
	freerdp_input_send_unicode_keyboard_event(handle->context->input, flags, unicode_code_point);
}

void prdp_disconnect(prdp_handle* handle)
{
	if (handle == NULL || handle->context == NULL)
		return;
	freerdp_abort_connect_context(handle->context);
}

void prdp_free(prdp_handle* handle)
{
	if (handle == NULL)
		return;
	if (handle->context != NULL)
	{
		prdpContext* ctx = (prdpContext*)handle->context;
		if (ctx->thread_started)
		{
			WaitForSingleObject(ctx->thread, INFINITE);
			CloseHandle(ctx->thread);
		}
		freerdp_client_context_free(handle->context);
	}
	free(handle);
#ifdef _WIN32
	/* Matches the WSAStartup in prdp_connect - only reached for a handle that made it past that
	   call (WSAStartup itself failing returns NULL before a handle is ever allocated), so this is
	   always 1:1 with a successful WSAStartup. */
	WSACleanup();
#endif
}

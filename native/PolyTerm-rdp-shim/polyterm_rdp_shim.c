/* PolyTerm RDP shim implementation. See polyterm_rdp_shim.h for the ABI contract. */

#include "polyterm_rdp_shim.h"

#include <stdlib.h>
#include <string.h>

#include <freerdp/freerdp.h>
#include <freerdp/client.h>
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

static BOOL prdp_end_paint(rdpContext* context)
{
	prdpContext* ctx = (prdpContext*)context;
	rdpGdi* gdi = context->gdi;

	if (gdi != NULL && ctx->on_framebuffer != NULL)
	{
		ctx->on_framebuffer(ctx->user_data, gdi->primary_buffer, gdi->width, gdi->height,
		                    (int32_t)gdi->stride);
	}
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
			ctx->on_state(ctx->user_data, 1, NULL);
		else
			ctx->on_state(ctx->user_data, 0, "freerdp_connect failed");
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
}

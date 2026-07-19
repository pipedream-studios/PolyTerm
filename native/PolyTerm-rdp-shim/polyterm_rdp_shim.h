/*
 * PolyTerm RDP shim — a deliberately thin C ABI over FreeRDP 3's client API (issue #67).
 * Bind this, not FreeRDP's full API — insulates the C# side from upstream FreeRDP churn.
 * Scope: connect/display/input only (first feature-ladder rung; see docs/polyterm-spec.md §5.2).
 */
#ifndef POLYTERM_RDP_SHIM_H
#define POLYTERM_RDP_SHIM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C"
{
#endif

typedef struct prdp_handle prdp_handle;

/*
 * Fired on every completed frame (FreeRDP's own update->EndPaint). buffer points at FreeRDP's own
 * GDI backbuffer (BGRA32, see stride for row layout) - it is only valid for the duration of this
 * callback, on FreeRDP's own internal thread. Copy it if you need it afterward; do not free it.
 */
typedef void (*prdp_framebuffer_cb)(void* user_data, const uint8_t* buffer, int32_t width,
                                    int32_t height, int32_t stride);

/* state: 1 = connected, 0 = disconnected (clean or after an error - message is non-null on error). */
typedef void (*prdp_state_cb)(void* user_data, int32_t state, const char* message);

/*
 * Connects asynchronously: spawns a background thread that runs FreeRDP's own connect + message
 * loop for the lifetime of the session. Returns NULL immediately on a same-thread setup failure
 * (bad arguments, out of memory); a later connection failure (auth, network, protocol) is reported
 * through on_state instead, since it only happens once the background thread starts running.
 */
prdp_handle* prdp_connect(const char* host, int32_t port, const char* username,
                          const char* password, const char* domain, int32_t width, int32_t height,
                          prdp_framebuffer_cb on_framebuffer, prdp_state_cb on_state,
                          void* user_data);

/* PTR_FLAGS_* from FreeRDP's own input.h (MOVE=0x0800, BUTTON1=0x1000, BUTTON2=0x2000, BUTTON3=0x4000, DOWN=0x8000). */
void prdp_send_mouse(prdp_handle* handle, uint16_t flags, int32_t x, int32_t y);

/* rdp_scancode: FreeRDP's own RDP_SCANCODE_* encoding (see freerdp/scancode.h). */
void prdp_send_key(prdp_handle* handle, int32_t down, uint32_t rdp_scancode);

/* Requests disconnect; on_state(0, NULL) fires once the background thread has actually stopped. */
void prdp_disconnect(prdp_handle* handle);

/* Releases all resources. Call only after on_state has reported disconnection. */
void prdp_free(prdp_handle* handle);

#ifdef __cplusplus
}
#endif

#endif /* POLYTERM_RDP_SHIM_H */

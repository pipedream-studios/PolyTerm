/*
 * Text clipboard sync over RDP's cliprdr virtual channel (issue #389). Internal to the shim: the
 * public ABI is prdp_clipboard_cb and prdp_set_clipboard_text in polyterm_rdp_shim.h.
 *
 * The shim passes text through as raw CF_UNICODETEXT bytes and never looks inside them: line-ending
 * conversion, termination, size limits and echo suppression all live on the C# side.
 */
#ifndef POLYTERM_RDP_CLIPBOARD_H
#define POLYTERM_RDP_CLIPBOARD_H

#include "polyterm_rdp_shim.h"

#include <freerdp/client/cliprdr.h>
#include <winpr/synch.h>

typedef struct
{
	/* Guards everything below it that changes after init: text is set from the embedding
	   application's thread, while the channel callbacks run on FreeRDP's. */
	CRITICAL_SECTION lock;
	int initialized;

	CliprdrClientContext* channel; /* NULL unless the cliprdr channel is connected */
	int ready;                     /* the server has sent Monitor Ready on this channel */
	BYTE* text;                    /* the local clipboard's CF_UNICODETEXT bytes, or NULL */
	UINT32 text_length;

	/* Set before the connection thread starts, then only read. */
	prdp_clipboard_cb on_text;
	void* user_data;
} prdp_clipboard;

void prdp_clipboard_init(prdp_clipboard* clipboard);

/* Safe to call on a zeroed struct that was never initialized. */
void prdp_clipboard_uninit(prdp_clipboard* clipboard);

/* Call from the ChannelConnected/ChannelDisconnected handlers for the cliprdr channel. */
void prdp_clipboard_attach(prdp_clipboard* clipboard, CliprdrClientContext* channel);
void prdp_clipboard_detach(prdp_clipboard* clipboard);

/* Copies data. NULL data or a length of 0 or less means there's no local text. */
void prdp_clipboard_set_text(prdp_clipboard* clipboard, const uint8_t* data, int32_t length);

#endif /* POLYTERM_RDP_CLIPBOARD_H */

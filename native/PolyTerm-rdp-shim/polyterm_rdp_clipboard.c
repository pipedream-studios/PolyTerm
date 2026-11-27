/* See polyterm_rdp_clipboard.h. The flow is MS-RDPECLIP's: whoever copies announces a format list,
   and the data itself is only transferred when the other side asks for it. We ask as soon as the
   server announces Unicode text, because the embedding application wants the text itself rather than
   a promise to fetch it later. */

#ifdef _WIN32
#include <winsock2.h> /* before windows.h - see polyterm_rdp_shim.c */
#endif

#include "polyterm_rdp_clipboard.h"

#include <stdlib.h>
#include <string.h>

#include <freerdp/channels/cliprdr.h>
#include <winpr/user.h>

/* Tells the server what the local clipboard now holds. The caller holds clipboard->lock. Sending
   only queues the PDU on FreeRDP's channel manager, so it doesn't block and is safe off FreeRDP's
   own thread. */
static UINT prdp_clipboard_announce(prdp_clipboard* clipboard)
{
	CLIPRDR_FORMAT format = { 0 };
	format.formatId = CF_UNICODETEXT;

	CLIPRDR_FORMAT_LIST list = { 0 };
	list.common.msgType = CB_FORMAT_LIST;
	list.numFormats = clipboard->text != NULL ? 1 : 0;
	list.formats = &format;
	return clipboard->channel->ClientFormatList(clipboard->channel, &list);
}

static UINT prdp_clipboard_monitor_ready(CliprdrClientContext* channel,
                                         const CLIPRDR_MONITOR_READY* monitor_ready)
{
	(void)monitor_ready;
	prdp_clipboard* clipboard = (prdp_clipboard*)channel->custom;

	CLIPRDR_GENERAL_CAPABILITY_SET general = { 0 };
	general.capabilitySetType = CB_CAPSTYPE_GENERAL;
	general.capabilitySetLength = CB_CAPSTYPE_GENERAL_LEN;
	general.version = CB_CAPS_VERSION_2;
	general.generalFlags = CB_USE_LONG_FORMAT_NAMES;

	CLIPRDR_CAPABILITIES capabilities = { 0 };
	capabilities.cCapabilitiesSets = 1;
	capabilities.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general;

	UINT rc = channel->ClientCapabilities(channel, &capabilities);
	if (rc != CHANNEL_RC_OK)
		return rc;

	/* The protocol requires a format list here, even an empty one, to finish initialization: without
	   it Windows never reports what's copied on the host. Any list replaces the host's clipboard, so
	   with no local text this empties it - including what was there before a reconnect. */
	EnterCriticalSection(&clipboard->lock);
	clipboard->ready = 1;
	rc = prdp_clipboard_announce(clipboard);
	LeaveCriticalSection(&clipboard->lock);
	return rc;
}

/* The remote side copied something. */
static UINT prdp_clipboard_server_format_list(CliprdrClientContext* channel,
                                              const CLIPRDR_FORMAT_LIST* list)
{
	int has_text = 0;
	for (UINT32 i = 0; i < list->numFormats; i++)
	{
		if (list->formats[i].formatId == CF_UNICODETEXT)
			has_text = 1;
	}

	CLIPRDR_FORMAT_LIST_RESPONSE response = { 0 };
	response.common.msgType = CB_FORMAT_LIST_RESPONSE;
	response.common.msgFlags = CB_RESPONSE_OK;
	UINT rc = channel->ClientFormatListResponse(channel, &response);
	if (rc != CHANNEL_RC_OK || !has_text)
		return rc;

	CLIPRDR_FORMAT_DATA_REQUEST request = { 0 };
	request.common.msgType = CB_FORMAT_DATA_REQUEST;
	request.requestedFormatId = CF_UNICODETEXT;
	return channel->ClientFormatDataRequest(channel, &request);
}

/* The remote side is pasting what we announced. */
static UINT prdp_clipboard_server_data_request(CliprdrClientContext* channel,
                                               const CLIPRDR_FORMAT_DATA_REQUEST* request)
{
	prdp_clipboard* clipboard = (prdp_clipboard*)channel->custom;

	CLIPRDR_FORMAT_DATA_RESPONSE response = { 0 };
	response.common.msgType = CB_FORMAT_DATA_RESPONSE;

	EnterCriticalSection(&clipboard->lock);
	if (request->requestedFormatId == CF_UNICODETEXT && clipboard->text != NULL)
	{
		response.common.msgFlags = CB_RESPONSE_OK;
		response.common.dataLen = clipboard->text_length;
		response.requestedFormatData = clipboard->text;
	}
	else
		response.common.msgFlags = CB_RESPONSE_FAIL;
	/* Still under the lock: FreeRDP serializes the data into its own buffer during this call. */
	UINT rc = channel->ClientFormatDataResponse(channel, &response);
	LeaveCriticalSection(&clipboard->lock);
	return rc;
}

/* The text we asked for in prdp_clipboard_server_format_list, which is the only format we ever
   request. Deliberately not under the lock: the callback runs the application's own code. */
static UINT prdp_clipboard_server_data_response(CliprdrClientContext* channel,
                                                const CLIPRDR_FORMAT_DATA_RESPONSE* response)
{
	prdp_clipboard* clipboard = (prdp_clipboard*)channel->custom;

	if ((response->common.msgFlags & CB_RESPONSE_OK) && clipboard->on_text != NULL &&
	    response->requestedFormatData != NULL && response->common.dataLen > 0 &&
	    response->common.dataLen <= INT32_MAX)
	{
		clipboard->on_text(clipboard->user_data, response->requestedFormatData,
		                   (int32_t)response->common.dataLen);
	}
	return CHANNEL_RC_OK;
}

void prdp_clipboard_init(prdp_clipboard* clipboard)
{
	InitializeCriticalSection(&clipboard->lock);
	clipboard->initialized = 1;
}

void prdp_clipboard_uninit(prdp_clipboard* clipboard)
{
	if (!clipboard->initialized)
		return;
	free(clipboard->text);
	clipboard->text = NULL;
	DeleteCriticalSection(&clipboard->lock);
	clipboard->initialized = 0;
}

void prdp_clipboard_attach(prdp_clipboard* clipboard, CliprdrClientContext* channel)
{
	channel->custom = clipboard;
	channel->MonitorReady = prdp_clipboard_monitor_ready;
	channel->ServerFormatList = prdp_clipboard_server_format_list;
	channel->ServerFormatDataRequest = prdp_clipboard_server_data_request;
	channel->ServerFormatDataResponse = prdp_clipboard_server_data_response;

	EnterCriticalSection(&clipboard->lock);
	clipboard->channel = channel;
	clipboard->ready = 0;
	LeaveCriticalSection(&clipboard->lock);
}

void prdp_clipboard_detach(prdp_clipboard* clipboard)
{
	EnterCriticalSection(&clipboard->lock);
	clipboard->channel = NULL;
	clipboard->ready = 0;
	LeaveCriticalSection(&clipboard->lock);
}

void prdp_clipboard_set_text(prdp_clipboard* clipboard, const uint8_t* data, int32_t length)
{
	BYTE* copy = NULL;
	if (data != NULL && length > 0)
	{
		copy = (BYTE*)malloc((size_t)length);
		if (copy == NULL)
			return;
		memcpy(copy, data, (size_t)length);
	}

	EnterCriticalSection(&clipboard->lock);
	free(clipboard->text);
	clipboard->text = copy;
	clipboard->text_length = copy != NULL ? (UINT32)length : 0;
	/* Before Monitor Ready there's nothing to do: prdp_clipboard_monitor_ready announces then. A
	   failed send isn't reported: the channel is on its way down, and the session will say so. */
	if (clipboard->channel != NULL && clipboard->ready)
		(void)prdp_clipboard_announce(clipboard);
	LeaveCriticalSection(&clipboard->lock);
}

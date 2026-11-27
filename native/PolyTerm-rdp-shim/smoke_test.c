/* Standalone smoke test - not part of the shipped shim. Connects to a real RDP host, prints the
   first few framebuffer callbacks' dimensions, then disconnects.
   Usage: smoke_test <host> <user> <pass> [domain] [share-name share-path] */
#include "polyterm_rdp_shim.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <stdatomic.h>

static atomic_int frame_count = 0;
static atomic_int connected = 0;
static atomic_int disconnected = 0;

static void on_framebuffer(void* user_data, const uint8_t* buffer, int32_t width, int32_t height,
                           int32_t region_x, int32_t region_y, int32_t region_width,
                           int32_t region_height, int32_t stride)
{
	(void)user_data;
	(void)region_x;
	(void)region_y;
	(void)region_width;
	(void)region_height;
	int n = atomic_fetch_add(&frame_count, 1);
	if (n < 3)
	{
		long sum = 0;
		for (int i = 0; i < width * height * 4 && i < 4096; i++) sum += buffer[i];
		printf("frame %d: %dx%d stride=%d firstBytesSum=%ld\n", n, width, height, stride, sum);
		fflush(stdout);
	}
}

static void on_state(void* user_data, int32_t state, const char* message)
{
	(void)user_data;
	if (state == 1)
	{
		printf("state: CONNECTED\n");
		atomic_store(&connected, 1);
	}
	else
	{
		printf("state: DISCONNECTED (%s)\n", message ? message : "clean");
		atomic_store(&disconnected, 1);
	}
	fflush(stdout);
}

int main(int argc, char** argv)
{
	if (argc < 4)
	{
		fprintf(stderr, "usage: %s <host> <user> <pass> [domain] [share-name share-path]\n", argv[0]);
		return 2;
	}
	const char* domain = argc > 4 ? argv[4] : "";
	const char* drive_names[1] = { argc > 6 ? argv[5] : NULL };
	const char* drive_paths[1] = { argc > 6 ? argv[6] : NULL };
	int32_t drive_count = argc > 6 ? 1 : 0;

	prdp_handle* h = prdp_connect(argv[1], 3389, argv[2], argv[3], domain, 1024, 768, drive_names,
	                              drive_paths, drive_count, 1, on_framebuffer, on_state, NULL, NULL);
	if (h == NULL)
	{
		fprintf(stderr, "prdp_connect returned NULL\n");
		return 1;
	}

	for (int i = 0; i < 100 && !atomic_load(&disconnected); i++)
	{
		usleep(200000);
		if (atomic_load(&connected) && atomic_load(&frame_count) >= 3)
			break;
	}

	int got_connected = atomic_load(&connected);
	int got_frames = atomic_load(&frame_count);
	printf("summary: connected=%d frames=%d\n", got_connected, got_frames);

	prdp_disconnect(h);
	for (int i = 0; i < 50 && !atomic_load(&disconnected); i++) usleep(100000);
	prdp_free(h);

	return (got_connected && got_frames > 0) ? 0 : 1;
}

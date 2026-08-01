#include "polyterm_pty_shim.h"

#include <errno.h>
#include <string.h>
#include <sys/ioctl.h>

int polyterm_pty_set_winsize(int fd, unsigned short rows, unsigned short cols)
{
    struct winsize ws;
    memset(&ws, 0, sizeof(ws));
    ws.ws_row = rows;
    ws.ws_col = cols;

    if (ioctl(fd, TIOCSWINSZ, &ws) < 0)
        return -errno;

    return 0;
}

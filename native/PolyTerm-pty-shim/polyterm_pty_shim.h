#ifndef POLYTERM_PTY_SHIM_H
#define POLYTERM_PTY_SHIM_H

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Sets a PTY's kernel-recorded window size via ioctl(TIOCSWINSZ) - a fixed-arity C wrapper around
 * the real ioctl(2), which is declared variadic in libc (int ioctl(int fd, unsigned long request,
 * ...)). Calling a true variadic C function directly through a fixed-signature .NET P/Invoke
 * declaration is unreliable on Apple Silicon's ABI, which passes variadic arguments differently
 * (stack-only) than named/fixed ones - confirmed against real CI macOS runners: direct P/Invoke
 * reported success while the kernel's own immediate read-back showed the size was never actually
 * written. This shim exists so a real C compiler handles the variadic call correctly; .NET only
 * ever calls this fixed, non-variadic exported function.
 *
 * Returns 0 on success, -errno on failure.
 */
int polyterm_pty_set_winsize(int fd, unsigned short rows, unsigned short cols);

#ifdef __cplusplus
}
#endif

#endif

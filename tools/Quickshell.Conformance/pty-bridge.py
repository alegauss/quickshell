# A Linux pty whose other end is a TCP socket, so esctest's every byte reaches the emulator (QS211).
#
# Through the Windows pseudo-console, conhost answered esctest's queries itself - DECRQCRA came back
# as conhost's 0000 and DA1 as conhost's attributes - and the emulator never saw them. Here nothing
# sits between the suite and the terminal it judges: this listens on 127.0.0.1, takes one
# connection, runs the command on a pty of the size asked for, and copies bytes both ways
# unchanged until the command exits. It then exits with the command's own status.
#
#   python3 - <port> <columns> <rows> <command...>      (the script itself arrives on stdin)

import errno
import fcntl
import os
import pty
import select
import socket
import struct
import sys
import termios


def main():
    port, columns, rows = int(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3])
    command = sys.argv[4:]

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", port))
    listener.listen(1)

    # Said on stdout, which the conformance tool reads, so it connects only once there is a
    # listener and never races the bind.
    print("listening", flush=True)

    connection, _ = listener.accept()
    listener.close()
    connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    child, master = pty.fork()

    if child == 0:
        os.execvp(command[0], command)

    fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))

    open_ends = {master, connection.fileno()}

    while master in open_ends:
        readable, _, _ = select.select(list(open_ends), [], [])

        for end in readable:
            try:
                data = os.read(end, 65536)
            except OSError as failure:
                # EIO is how a pty master says its child has gone and its output is all read.
                if failure.errno != errno.EIO:
                    raise
                data = b""

            if not data:
                open_ends.discard(end)
                continue

            if end == master:
                connection.sendall(data)
            else:
                os.write(master, data)

    _, status = os.waitpid(child, 0)
    connection.close()

    sys.exit(os.waitstatus_to_exitcode(status))


main()

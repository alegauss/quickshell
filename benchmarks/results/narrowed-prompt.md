# A bash prompt in a pane a split narrows — QS350

Traced on **2026-10-16** against the Docker fixture's Debian sshd (`prototypes/SshProbe/fixture`,
bash **5.2.21(1)-release**) by `NarrowedPromptTests`. Re-run it with the fixture up:

```
cd prototypes\SshProbe\fixture && docker compose up -d target
tests\Quickshell.App.Tests\bin\Debug\net10.0-windows\Quickshell.App.Tests.exe --filter-class "*NarrowedPromptTests"
```

The pane opened at 80 columns. A line wider than 40 was printed and the prompt came back. The pane
was then narrowed to 40 columns, as a split does: the model first, then the window change to the
server. The session's recording holds every byte the host sent.

## What bash sent

Everything after the prompt that followed the marker, which is bash's whole answer to the window
change:

```
<CR><ESC>[K<CR><ESC>]0;probe@qs-sshd-target: ~<BEL><ESC>[01;32mprobe@qs-sshd-target<ESC>[00m:<ESC>[01;34m~<ESC>[00m$
```

Back to the line's start, the line cleared, back to the start again, the title, and the prompt. There
is **no line feed**. Bash redraws its prompt on the row it is on and adds no line.

## What the screen showed

One run, rows as the screen held them (`w` marks a row that wraps into the next):

```
at 80 columns                            at 40 columns, before bash answered
0 |Connecting to probe@127.0.0.1:2222... 0 |Connecting to probe@127.0.0.1:2222...
1 |Last login: Fri Oct 16 14:26:02 2026…  1w|Last login: Fri Oct 16 14:26:02 2026 fro
2 |echo $BASH_VERSION; echo MARK-$((6*7)) 2 |m 172.23.0.1
3 |probe@qs-sshd-target:~$ echo $BASH_V…  3 |echo $BASH_VERSION; echo MARK-$((6*7))
4 |5.2.21(1)-release                      4w|probe@qs-sshd-target:~$ echo $BASH_VERSI
5 |MARK-42                                5 |ON; echo MARK-$((6*7))
6 |probe@qs-sshd-target:~$                6 |5.2.21(1)-release
cursor 6,24                              7 |MARK-42
                                         8 |probe@qs-sshd-target:~$
                                         cursor 8,24
```

The prompt moved down two rows because two lines above it were wider than 40 columns and wrapped.
The cursor moved with it. After bash's redraw the screen was the same: the prompt once, on the
cursor's row, and nothing below it.

## Conclusion

The extra line a split seems to leave above a bash prompt is **this client reflowing the lines above
it**, which is what a terminal that reflows on resize does, Windows Terminal included. It is not bash
printing a second prompt, and not a stray line from the client. There is no defect to fix. KEYS.md
says so under Panes.

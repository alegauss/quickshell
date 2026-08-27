// The terminals on this page. They are HTML rather than JSX because a terminal is a block
// of pre-formatted text with colour on individual runs, and expressing that as elements
// would put the layout of a screen into the component tree. Each string is injected into a
// <pre> inside a .term, which is always dark: this client draws its own grid, and a
// screenshot that inverts with the page toggle is a screenshot of nothing.
//
// Every line here is illustrative. Nothing in this file is a captured run, and no figure
// appears in it that is not either a stated budget or a constant of the protocol.

/** A live session: the connection line, wide characters, and the cursor where it sits. */
export const heroTerminal = `<span class="c">Trying 10.42.7.19:22 …</span>
<span class="rem">prod-eu-1</span>  host key <span class="ok">known</span>  ed25519 SHA256:9cE2r+wQ…kQ4  aes256-gcm@openssh.com
<span class="c">Last login: Thu Aug 27 09:14:02 2026 from 10.4.2.19</span>

<span class="ok">alex@prod-eu-1</span>:<span class="rem">~</span>$ printf '%s\\n' 'ascii │ 日本語 │ café │ 🚀 done'
ascii │ 日本語 │ café │ 🚀 done
<span class="ok">alex@prod-eu-1</span>:<span class="rem">~</span>$ systemctl is-active nginx postgresql
<span class="ok">active</span>
<span class="ok">active</span>
<span class="ok">alex@prod-eu-1</span>:<span class="rem">~</span>$ tail -f /var/log/nginx/access.log
10.4.2.19 - - [27/Aug/2026:09:14:38] "GET /health" <span class="ok">200</span> 2
10.4.2.19 - - [27/Aug/2026:09:14:41] "POST /orders" <span class="ok">201</span> 511
10.4.2.19 - - [27/Aug/2026:09:14:43] "GET /orders/8812" <span class="fail">502</span> 166
<span class="cur"> </span>`;

/** A drop the session survived, and a host key it refused. */
export const sessionTerminal = `<span class="warn">link lost</span>  prod-eu-1  no data for 4.0 s
<span class="c">  reconnecting …  attempt 1 refused (network unreachable)</span>
<span class="c">  reconnecting …  attempt 2 refused (network unreachable)</span>
<span class="ok">restored</span>   prod-eu-1  on attempt 3, after 11.4 s
<span class="c">  scrollback kept, 18 240 rows · tab kept · 3 forwards restored</span>
<span class="c">  forward -L 5432 → db-1:5432 <span class="ok">up</span> · -R 9000 <span class="ok">up</span> · SOCKS 1080 <span class="ok">up</span></span>

<span class="fail">REFUSED</span>  build-runner-4  the host key does not match the one on record
  <span class="c">stored </span> ed25519 SHA256:UkP0m1s+7Jd…Ax8   first seen 2026-03-04
  <span class="c">offered</span> ed25519 SHA256:t7Qa9Rr2Zz…bN1   <span class="fail">not accepted</span>
  <span class="c">This is what a machine in the middle looks like. It is also what a rebuilt</span>
  <span class="c">host looks like. Remove the stored entry deliberately to tell them apart.</span>
<span class="cur"> </span>`;

/** What a remote program is answered, and what it is refused. */
export const protocolTerminal = `<span class="c">quickshell · protocol trace · session prod-eu-1 · secrets redacted</span>

<span class="rem">→ host</span>  CSI c                        <span class="c">"what are you"</span>
<span class="ok">← us</span>    ESC [ ? 65 ; 1 ; 9 ; 15 ; 22 c   <span class="c">a VT500-series terminal, and here is what it has</span>

<span class="rem">→ host</span>  OSC 52 ; c ; ?  BEL          <span class="c">"read me the clipboard"</span>
<span class="fail">← us</span>    (nothing)                    <span class="c">refused: write-only when on, off by default</span>

<span class="rem">→ host</span>  OSC 0 ; deploy: staging  BEL <span class="c">set the window title</span>
<span class="ok">← us</span>    (nothing)                    <span class="c">title set, never reported back</span>

<span class="rem">→ you</span>   click at row 12, column 240
<span class="warn">← us</span>    <span class="warn">X10 encoding cannot name column 240; asked the host for SGR (1006)</span>
        <span class="c">the alternative is naming a different cell and calling it a click</span>
<span class="cur"> </span>`;

// The copy lives here and nowhere else. Every section component imports a value from this
// module and only renders it, so a claim is an array element a reviewer can check against
// the product rather than a string welded into the markup that displays it. The composition
// (which section, in which order, and the illustrative terminals) lives in the JSX; this
// file is the words.
//
// Fragments carrying inline code or emphasis are modelled as a small tagged run list
// (`Rich`) rather than raw HTML, so a section renders them without dangerouslySetInnerHTML
// and the Markdown twin has a structure to convert rather than markup to parse.
//
// House rule, borrowed from the sibling site: no em dashes in published prose. Where one
// would go, the relation the sentence actually has is written out.
//
// ---------------------------------------------------------------------------------------
// WHAT THIS PAGE ASSUMES. The copy below describes Quickshell with every block of
// docs/ROADMAP.md delivered: the transport, the key handling, the session tree, SFTP, the
// forwards, the shell and the packaging. Blocks C and H are partly shipped today and the
// rest is open, so this is the page the finished client gets rather than a report on the
// one in the tree. Every figure it states is the budget from docs/PERFORMANCE.md or a
// "Done when" criterion from the roadmap, quoted as the threshold it is; none of them is a
// measurement invented here.
// ---------------------------------------------------------------------------------------

export type Run =
  | string
  | { code: string }
  | { b: string }
  | { i: string };

export type Rich = Run[];

/* ------------------------------------------------------------------ meta + chrome */

export const meta = {
  title: "Quickshell: SSH on Windows, without the suite around it",
  description:
    "A native Windows SSH client that draws the terminal grid on the GPU: one instanced draw call per frame, none at all when nothing changed, and a cold start to a live prompt under 400 ms. Sessions, keys, SFTP and port forwards, and nothing bolted on beside them.",
  og: {
    title: "Quickshell",
    description:
      "The terminal grid on the GPU, one draw call a frame and none while you read. Sessions, keys, SFTP and forwards on Windows, with a stated list of what it will never grow.",
    url: "https://alegauss.github.io/quickshell/",
  },
} as const;

export const repoUrl = "https://github.com/alegauss/quickshell";
export const parentUrl = "https://alegauss.github.io/";

// The release page rather than a file: the installer carries its version in its name, so
// there is no version-independent URL for the asset itself. `releases/latest` is the one
// link that cannot go stale, and it is also where the checksum lives.
export const releasesUrl = `${repoUrl}/releases/latest`;

export const navLinks = [
  { href: "#session", label: "Session" },
  { href: "#terminal", label: "Terminal" },
  { href: "#shell", label: "Window" },
  { href: "#numbers", label: "Numbers" },
] as const;

export const footer = {
  links: [
    { href: repoUrl, label: "GitHub" },
    { href: releasesUrl, label: "Releases" },
    { href: `${repoUrl}/blob/main/docs/ROADMAP.md`, label: "Roadmap" },
    { href: `${repoUrl}/blob/main/docs/PERFORMANCE.md`, label: "Performance budget" },
    { href: `${repoUrl}/blob/main/docs/CHANGELOG.md`, label: "Changelog" },
  ],
  disclaimer:
    "Unofficial / community project, not affiliated with, endorsed by, or sponsored by Mobatek, Simon Tatham or the OpenSSH project. “MobaXterm” is a trademark of Mobatek; PuTTY and OpenSSH are named here only because Quickshell reads the files they write. The SSH protocol itself is not implemented in this repository: the transport sits behind a seam, and the client's own code is the terminal, the renderer and everything a person touches. © 2026 Alexandre Oliveira.",
} as const;

/* --------------------------------------------------------------- sponsor */

// Mirrors alegauss.github.io/sponsor.json, the canonical sponsor declaration for these
// projects. Transcribed here rather than fetched at runtime: this site is prerendered, and
// the whole point of naming a sponsor is that crawlers and LLMs read it in the served HTML.
export const sponsor = {
  label: "Sponsored by",
  name: "Viglet",
  url: "https://www.viglet.org",
  siteLabel: "viglet.org",
  logo: "/quickshell/viglet/viglet-logo.png",
  summary:
    "Open source search and content tools for organisations with a lot to publish. Run on your own servers, with no per-user licence.",
  products: [
    {
      name: "Viglet Turing ES",
      url: "https://turing.viglet.org",
      logo: "/quickshell/viglet/turing-logo.png",
      inline:
        "so visitors find what they came for, with AI answers drawn only from your own content",
    },
    {
      name: "Viglet Shio CMS",
      url: "https://shio.viglet.org",
      logo: "/quickshell/viglet/shio-logo.png",
      inline:
        "so a new page goes live the same day, reviewed and approved by your own team",
    },
  ],
} as const;

/* ------------------------------------------------------------------ hero */

export const hero = {
  badge: "Windows 10 / 11 · x64 · Per-user install",
  titleLead: "SSH on Windows,",
  titleAccent: "without the suite around it.",
  sub: [
    "Quickshell draws the terminal grid on the GPU: one ",
    { code: "DrawInstanced" },
    " of twenty-byte cells per frame, and no draw call at all on a frame where nothing changed. Sessions, keys, SFTP and port forwards are in the window. Nothing else is, and the list of what will never be added is published.",
  ] as Rich,
  // No emoji on these three, and that is a writing rule rather than a taste: an emoji glued
  // to the front of a feature line is the most recognisable mannerism of a generated landing
  // page, and these strings are also bullets in the Markdown twin an agent reads.
  meta: [
    "No X server, no bundled userland",
    "No telemetry, no account",
    "No background service",
  ],
  pills: [
    [{ b: ".NET 10" }, ", WPF host with a child HWND per pane"] as Rich,
    ["One ", { b: "D3D11" }, " device, one glyph atlas, one draw call"] as Rich,
    [{ b: "Zero allocations" }, " in the parse path"] as Rich,
    ["Widths generated from ", { b: "Unicode 17.0.0" }] as Rich,
  ],
};

export const heroTerminal = {
  title: "quickshell · prod-eu-1",
  caption: [
    "The prompt is drawable ",
    { b: "under 400 ms" },
    " from process creation. The second line is the one that catches most terminals out: Latin, CJK and emoji in one row, each taking the number of cells the remote shell believes it took.",
  ] as Rich,
};

/* ------------------------------------------------------------------ why */

export const why = {
  eyebrow: "Why leave the incumbent",
  heading: "The terminal is the product, not one tab of a toolkit",
  intro: [
    "The clients this replaces grew an X server, a POSIX userland, a macro recorder and a plugin host around a terminal that still redraws the whole screen to move a cursor. Quickshell is the other trade: a small surface, drawn properly, with the cost of every part written down before it was built.",
  ] as Rich,
  cards: [
    {
      icon: "🖥",
      title: "The grid is on the GPU",
      body: [
        "DirectWrite rasterises each glyph once into an atlas keyed on its subpixel offset, so a character keeps its weight in every column. The whole visible grid is then one ",
        { code: "DrawInstanced" },
        " of twenty-byte cells, blended in linear light so text weighs the same on either background.",
      ] as Rich,
    },
    {
      icon: "🌙",
      title: "Nothing to draw while you read",
      body: [
        "Every mutation moves a generation the renderer compares against the one it last drew, and a scroll dirties one row rather than the screen. When the host stops writing, the window authorises no further frame: an idle Quickshell submits zero draw calls and holds no raised timer resolution.",
      ] as Rich,
    },
    {
      icon: "⚡",
      title: "Echo does not queue behind output",
      body: [
        "Output volume is the host's choice and echo latency is what you feel, so the two never share a queue. Under a hundred-megabyte ",
        { code: "cat" },
        " the delay from key to glyph is the same as it is at rest, because frames are dropped under load and bytes never are.",
      ] as Rich,
    },
    {
      icon: "🔑",
      title: "The host you think you reached",
      body: [
        "Every connection is checked against ",
        { code: "known_hosts" },
        ", written in the format OpenSSH reads back unchanged. A key that has changed raises a dialog with no default accept button: the old entry has to be removed deliberately, because a client that defaults to accept is a client whose encryption is decoration.",
      ] as Rich,
    },
    {
      icon: "🗂",
      title: "Your ssh_config is the session tree",
      body: [
        "Patterns, ",
        { code: "Include" },
        " and ",
        { code: "ProxyJump" },
        " chains are read as written, so a host you already defined for OpenSSH opens here without being defined a second time. MobaXterm and PuTTY sessions import, and every setting this client will not honour is named per session rather than dropped.",
      ] as Rich,
    },
    {
      icon: "📐",
      title: "The numbers are in the repository",
      body: [
        "Six figures, the machine they are measured on and the method that settles each one live in ",
        { code: "docs/PERFORMANCE.md" },
        ", and a regression past any of them fails a build rather than reaching a release. A number without a machine is a mood.",
      ] as Rich,
    },
  ],
};

/* --------------------------------------------------------------- the session */

export const session = {
  eyebrow: "Blocks A and B",
  heading: "A session that stays up, or says why it did not",
  intro: [
    "A link that drops for ten seconds is ordinary on mobile and on a VPN, so it costs a reconnect and not the session. What cannot be recovered is said in a sentence naming the hop, the method and the remedy.",
  ] as Rich,
  points: [
    [
      "Reconnect keeps the tab",
      ": the scrollback, the pane layout and every forward come back on the session that dropped, and the client states which attempt restored it rather than reopening a blank tab beside the dead one.",
    ],
    [
      "Failures are named, not raised",
      ": a wrong port, a refused algorithm, a rejected key and an expired second factor are four messages with four remedies, because the error is the documentation a user reads at the moment something fails.",
    ],
    [
      "Keys, agents and tokens",
      ": ed25519 and RSA files, keyboard-interactive and two factors, and identities held only in the Windows agent or Pageant, which is the one route to a key on a hardware token that this client may never hold.",
    ],
    [
      "Passwords never rest in a managed string",
      ": what is stored is protected with DPAPI at user scope, so a copy of the store does not decrypt on another machine, and the settings surface says plainly that this does not stop an attacker already running as you.",
    ],
    [
      "Agent forwarding is a per-host decision",
      ": forwarding hands a remote machine the ability to authenticate as you everywhere, so it is answered per host rather than by a checkbox set once and forgotten.",
    ],
    [
      "Bastions are the ordinary path",
      ": a chain of jumps uses one code path at any depth, and the target's own host key is verified rather than the bastion's.",
    ],
  ] as [string, string][],
  terminalTitle: "a link that dropped, and a key that changed",
  note: [
    "The protocol itself is a dependency behind a seam, which is a stated non-goal: no SSH is implemented in this repository, and no library type reaches the terminal or the window.",
  ] as Rich,
};

/* --------------------------------------------------------------- the terminal */

export const terminal = {
  eyebrow: "Block C",
  heading: "Emulation that does not lie about the remote",
  intro: [
    "A terminal is judged by the programs that already exist, so this one is measured against them rather than against tests its own author wrote.",
  ] as Rich,
  points: [
    [
      "esctest above ninety per cent",
      ", with every remaining failure named individually and accounted for by a reason or a task id. A suite written by whoever wrote the parser tests that person's reading of the specification, which is the part most likely to be wrong.",
    ],
    [
      "A parser that allocates nothing",
      ": Williams' table over fourteen states and all 256 bytes, emitting events without a single allocation in steady state, asserted over a full corpus replay that fails the build rather than printing a number somebody reads later.",
    ],
    [
      "Text that survives a split read",
      ": a stateful decoder and UAX #29 clusters, so a multi-byte character cut in half by the network arrives whole, and a combining mark lands on the cell it belongs to.",
    ],
    [
      "Widths the host agrees with",
      ": a table generated from Unicode 17.0.0, checked by printing mixed Latin, CJK and emoji and comparing the cursor column against what the remote shell believes.",
    ],
    [
      "Reflow that is reversible",
      ": narrowing and widening the window restores the original rows exactly, proven by property tests over random buffers and width sequences rather than by dragging a window.",
    ],
    [
      "Pixels checked on five environments",
      ": the golden-image suite runs on NVIDIA, AMD, Intel integrated, WARP and inside an RDP session, because a driver bug is by definition the thing the machine that wrote the code cannot see.",
    ],
  ] as [string, string][],
  terminalTitle: "protocol trace · what a host is answered, and what it is not",
  note: [
    "What a hostile host is refused is decided too: ",
    { code: "OSC 52" },
    " cannot read your clipboard, the title is set and never reported back, and a paste is bracketed so the host cannot run it as though you had typed it.",
  ] as Rich,
};

/* --------------------------------------------------------------- files and ports */

export const transfer = {
  eyebrow: "Blocks E and F",
  heading: "Files and ports, on the session you already opened",
  intro: [
    "A transfer and a forward are channels on a connection that exists. Opening a second one would cost you a second password, a second second-factor and a second entry in someone's audit log, for no gain.",
  ] as Rich,
  files: {
    title: "SFTP as a thing a person operates",
    items: [
      {
        lead: "Browse without running a command",
        rest: [
          ": a pane on the same session, listing fifty thousand entries with the first screen visible immediately rather than when the last row arrives.",
        ] as Rich,
      },
      {
        lead: "A queue that resumes",
        rest: [
          ": progress, cancel, and a restart from the offset it reached, refused when the partial cannot be shown to be a prefix of the source rather than continued into a file unlike its source.",
        ] as Rich,
      },
      {
        lead: "Folders and collisions are policy",
        rest: [
          ": recursion is explicit and a name that already exists asks, because this is where a transfer tool quietly destroys data.",
        ] as Rich,
      },
      {
        lead: "Drag from Explorer",
        rest: [
          ": the interaction people try first, onto a session or onto a directory in the browser.",
        ] as Rich,
      },
      {
        lead: "Compare a local and a remote directory",
        rest: [
          ": the deploy-and-check loop, with what differs listed instead of eyeballed.",
        ] as Rich,
      },
      {
        lead: "SCP for the appliance that offers nothing else",
        rest: [": kept as a fallback, never as the primary path."] as Rich,
      },
    ],
  },
  ports: {
    title: "A forward is a lifecycle, not a checkbox",
    items: [
      {
        lead: "Local, remote and SOCKS",
        rest: [
          ": a database client reaching a machine it has no route to, a service on your laptop reachable from the host, and one proxy covering a whole remote network.",
        ] as Rich,
      },
      {
        lead: "Restored with the session",
        rest: [
          ": every forward comes back after a reconnect, or the client says which one did not and why.",
        ] as Rich,
      },
      {
        lead: "Loopback unless you say otherwise",
        rest: [
          ": no forward binds beyond ",
          { code: "127.0.0.1" },
          " without an explicit choice, on a machine with any number of interfaces.",
        ] as Rich,
      },
      {
        lead: "No listener outlives its session",
        rest: [
          ": closing a session under load leaves the process holding no listening socket, checked from outside the client.",
        ] as Rich,
      },
      {
        lead: "A list of what is running",
        rest: [
          ": a forward is invisible by nature, so the client shows its own listeners instead of sending you to ",
          { code: "netstat" },
          " to understand it.",
        ] as Rich,
      },
    ],
  },
};

/* --------------------------------------------------------------- the window */

export const shell = {
  eyebrow: "Block G",
  heading: "The clean interface, defended",
  intro: [
    "A first run on a clean profile shows a title bar and a terminal. Everything else is reachable and nothing else is resident on the screen, which is a decision the defaults make on your behalf and the settings let you undo.",
  ] as Rich,
  points: [
    [
      "Tabs and panes, one device between them",
      ": sixteen panes share one D3D11 device, one glyph atlas and one set of shaders, so atlas memory at sixteen panes differs from one pane only by the instance buffers.",
    ],
    [
      "Everything is in the palette",
      ": the action list is generated from the actions themselves and a test asserts the palette enumerates all of it, so an action that exists and cannot be found fails a build.",
    ],
    [
      "Broadcast to the panes you choose",
      ": the same command on eight hosts, typed once, because mistyping the eighth is how fleet work fails today.",
    ],
    [
      "Schemes in the formats people already share",
      ": iTerm2 and Windows Terminal colour schemes are read as published rather than retyped as twenty colours.",
    ],
    [
      "A screen reader reads the grid",
      ": a GPU surface is opaque to assistive technology by construction, so the text is published deliberately and the cursor is followed as a prompt is typed into.",
    ],
    [
      "Settings are a file you can move",
      ": versioned, hand-editable, and round-tripped without being reformatted, reordered or losing a key it did not recognise.",
    ],
  ] as [string, string][],
};

/* --------------------------------------------------------------- the numbers */

export const numbers = {
  eyebrow: "The budget",
  heading: "Six figures, one machine, and the method for each",
  intro: [
    "These were written before the code they bind, and each is changed only by a commit that argues for the change. A regression past any of them fails a build.",
  ] as Rich,
  figures: [
    {
      what: "Input to photon",
      value: "< 8.3 ms",
      unit: "one refresh at 120 Hz",
      body: [
        "A keystroke echoed by a local shell, from the key going down to the glyph being on the glass. Settled by a high-speed capture of key and screen in one frame, or an instrumented path that timestamps the input event and the present. Never by feel.",
      ] as Rich,
    },
    {
      what: "Parse throughput",
      value: "≥ 400 MB/s",
      unit: "sustained",
      body: [
        "Mixed text and escape sequences, end to end, on a headless harness with no renderer attached. Parsing must never be the reason output is slow, so it is measured where nothing else can be blamed for it.",
      ] as Rich,
    },
    {
      what: "Steady-state frame",
      value: "< 2 ms",
      unit: "200 × 50 grid",
      body: [
        "One filled grid redrawn continuously, GPU and CPU time, in a steady state rather than on the first frame. Leaving the frame budget almost entirely unspent for one pane is what keeps several panes affordable.",
      ] as Rich,
    },
    {
      what: "Idle cost",
      value: "0",
      unit: "draw calls",
      body: [
        "A window nobody is typing into: zero draw calls submitted over an idle interval, no measurable occupancy on any core, and no raised system timer resolution held by the process. This is the figure a laptop user feels as battery life, so ",
        { b: "small is not a pass here" },
        ".",
      ] as Rich,
    },
    {
      what: "Cold start",
      value: "< 400 ms",
      unit: "to a live prompt",
      body: [
        "Process creation to an interactive local shell, which means a prompt that accepts a keystroke and not a window that has appeared. Reported for a cold file cache, with the warm figure beside it where a cold one cannot be arranged.",
      ] as Rich,
    },
    {
      what: "Resident memory",
      value: "< 120 MB",
      unit: "one session, default scrollback",
      body: [
        "The private working set after the session has settled. It is also held ",
        { b: "flat across a seventy-two-hour soak" },
        ", because a number that passes on minute one and drifts is a leak that passed.",
      ] as Rich,
    },
  ],
  note: [
    "The reference machine is named in ",
    { code: "docs/PERFORMANCE.md" },
    ": an i7-14700, an RTX 4060, a 3840 × 2160 display at 60 Hz, on Windows 11 Pro. A second machine may be measured, and then the machine is named beside the number, because two machines quoted as one is how a regression becomes a rounding difference. ",
    { b: "A figure quoted without the run that produced it is not a measurement." },
  ] as Rich,
};

/* --------------------------------------------------------------- switching */

export const switching = {
  eyebrow: "Leaving MobaXterm",
  heading: "Your sessions come with you, and what does not is named",
  intro: [
    "The session tree is the artefact you built over years, and recreating it by hand is the real cost of changing client. It is also the reason most people never get past the first evening with a new one.",
  ] as Rich,
  steps: [
    {
      title: "Point it at what you have",
      body: [
        "An OpenSSH ",
        { code: "config" },
        ", a MobaXterm session file or a PuTTY registry export. Patterns, includes and jump chains are read as written rather than flattened into a list.",
      ] as Rich,
    },
    {
      title: "Read what was skipped",
      body: [
        "Every setting this client will not honour is reported per session, by name. An X11 setting or a macro is refused out loud, because a silent drop is a defect report three weeks later.",
      ] as Rich,
    },
    {
      title: "Keep both for a while",
      body: [
        "Nothing is rewritten in place: the files the incumbent reads are left as they are, and the ",
        { code: "known_hosts" },
        " this client writes is one OpenSSH reads back unchanged.",
      ] as Rich,
    },
  ],
};

/* --------------------------------------------------------------- non-goals */

export const nonGoals = {
  eyebrow: "Stated, not accidental",
  heading: "What this will never grow",
  intro: [
    "The full list is in the roadmap and it is not a wish list of things not yet reached. A client is lean because of what it refuses, and refusing it in writing is what makes the refusal hold.",
  ] as Rich,
  items: [
    {
      title: "No X11 server or forwarding",
      body: "The single largest thing the incumbent carries, and the reason its install is measured in gigabytes.",
    },
    {
      title: "No bundled POSIX userland",
      body: "No Cygwin, no BusyBox. Windows has a package manager and a WSL, and neither of them is this client's job.",
    },
    {
      title: "No RDP, VNC, Telnet, serial or FTP",
      body: "One protocol, done properly. A second protocol is a second product wearing the same window.",
    },
    {
      title: "No web runtime",
      body: "No Electron, no WebView2, no JavaScript terminal. The frame budget on this page is the whole argument.",
    },
    {
      title: "No second graphics backend",
      body: "D3D11 and nothing beside it. WARP covers the machine with no usable GPU, and a CPU-rasterised grid is refused outright.",
    },
    {
      title: "No feature carried over because MobaXterm has it",
      body: "Parity is not the goal, and a macro recorder, a scripting engine and a plugin host are all refused in the MVP.",
    },
  ],
};

/* --------------------------------------------------------------- download */

export const download = {
  eyebrow: "Install it",
  heading: "Per-user, signed, and no administrator prompt",
  cta: "Download for Windows",
  ctaShort: "Download",
  secondary: "Checksums and notes",
  intro: [
    "Windows 10 or 11, x64. It installs under ",
    { code: "%LOCALAPPDATA%" },
    " and asks for nothing else, which is what reaches a managed laptop with SmartScreen on.",
  ] as Rich,
  facts: [
    "No service, no scheduled task",
    "No account, no telemetry",
    "Settings in one file you can copy",
  ],
  note: [
    "The asset carries its version in its name and ",
    { code: "SHA256SUMS.txt" },
    " beside it is what a download is checked against. Building from source needs the .NET 10 SDK and Windows, and it is how a contributor works on this rather than how anybody has to run it.",
  ] as Rich,
};

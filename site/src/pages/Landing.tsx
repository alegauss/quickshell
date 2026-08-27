import { Nav } from "../components/Nav";
import { Footer } from "../components/Footer";
import { Hero } from "../components/sections/Hero";
import { Why } from "../components/sections/Why";
import { Session } from "../components/sections/Session";
import { Terminal } from "../components/sections/Terminal";
import { Transfer } from "../components/sections/Transfer";
import { Shell } from "../components/sections/Shell";
import { Numbers } from "../components/sections/Numbers";
import { Switching } from "../components/sections/Switching";
import { NonGoals } from "../components/sections/NonGoals";
import { Download } from "../components/sections/Download";

// The landing page. The section order is the argument rather than a feature list: why →
// the session → the emulator → files and ports → the window → the numbers → the switch →
// the non-goals → the download.
//
// It ends on the download, and nothing follows it. The reader who has read this far has
// already decided; a section after the install is one written for somebody who left.
export function Landing() {
  return (
    <>
      <Nav />
      <Hero />
      <Why />
      <Session />
      <Terminal />
      <Transfer />
      <Shell />
      <Numbers />
      <Switching />
      <NonGoals />
      <Download />
      <Footer />
    </>
  );
}

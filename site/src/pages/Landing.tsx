import { SiteButton, SiteHero, SiteRich } from "@viglet/viglet-design-system/site";
import { Footer, Nav } from "../components/Chrome";
import { HeroTerminal } from "../components/sections/HeroTerminal";
import { Cells } from "../components/ui/Cells";
import { download, hero, repoUrl } from "../lib/site-content";
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
      <SiteHero
        icon={{ src: "/quickshell/logo.svg", alt: "Quickshell logo", logoShape: "mark" }}
        badge={hero.badge}
        title={hero.titleLead}
        titleAccent={hero.titleAccent}
        sub={<SiteRich runs={hero.sub} />}
        // The first button scrolls to the section that says what the installer touches,
        // because that is the question between a reader and an install.
        actions={
          <>
            <SiteButton href="#download">{download.cta}</SiteButton>
            <SiteButton variant="ghost" href={repoUrl}>
              ★ View on GitHub
            </SiteButton>
          </>
        }
        feature={<HeroTerminal />}
        meta={hero.meta}
        pills={hero.pills.map((runs, i) => (
          <SiteRich runs={runs} key={i} />
        ))}
      >
        <Cells />
      </SiteHero>
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

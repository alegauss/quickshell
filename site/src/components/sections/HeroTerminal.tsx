import { SiteRich as Rich } from "@viglet/viglet-design-system/site";
import { heroTerminal } from "../../lib/site-content";
import { heroTerminal as heroTerminalHtml } from "../../lib/diagrams";

// The product's half of the landing hero (QS370): the design system's SiteHero draws the
// badge, the headline, the lead, the call to action, the meta line and the pills, and this
// is the session it shows under the call to action. It is content, so the twin keeps it.
export function HeroTerminal() {
  return (
    <figure style={{ maxWidth: "820px", margin: "0 auto", textAlign: "left" }}>
      <div className="term">
        <div className="bar">
          <i />
          <i />
          <i />
          <span>{heroTerminal.title}</span>
        </div>
        <pre dangerouslySetInnerHTML={{ __html: heroTerminalHtml }} />
      </div>
      <figcaption>
        <Rich runs={heroTerminal.caption} />
      </figcaption>
    </figure>
  );
}

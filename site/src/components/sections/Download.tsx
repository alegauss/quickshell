import { download, releasesUrl } from "../../lib/site-content";
import { SiteButton, SiteRich as Rich, SiteSectionHead } from "@viglet/viglet-design-system/site";

// The section the argument ends in: the reader who accepted it can have the thing. The two
// buttons carry data-twin="omit" for the same reason the hero's do, but the prose around
// them does not, because what the installer touches and what it needs are facts an agent
// evaluating this client is right to want.
export function Download() {
  return (
    <section id="download">
      <div className="vg-site-wrap">
        <div className="reveal">
          <SiteSectionHead eyebrow={download.eyebrow} title={download.heading}>
            <Rich runs={download.intro} />
          </SiteSectionHead>
        </div>
        {/* The hero's action row and meta line, by the design system's own classes. */}
        <div className="vg-site-hero-actions" data-twin="omit">
          <SiteButton href={releasesUrl}>{download.cta}</SiteButton>
          <SiteButton variant="ghost" href={releasesUrl}>
            {download.secondary}
          </SiteButton>
        </div>
        <div className="vg-site-hero-meta">
          {download.facts.map((fact) => (
            <span key={fact}>{fact}</span>
          ))}
        </div>
        <p
          style={{
            maxWidth: "720px",
            margin: "26px auto 0",
            textAlign: "center",
            color: "var(--vg-muted-foreground)",
            fontSize: ".9rem",
          }}
        >
          <Rich runs={download.note} />
        </p>
      </div>
    </section>
  );
}

import {
  SiteButton,
  SiteFooter,
  SiteNav,
  SiteSponsor,
  SiteThemeToggle,
} from "@viglet/viglet-design-system/site";
import {
  download,
  footer,
  navLinks,
  parentUrl,
  releasesUrl,
  repoUrl,
  sponsor,
  THEME_KEY,
} from "../lib/site-content";
import { ConsentSettings } from "./Consent";
import { Cells } from "./ui/Cells";

// The chrome every route shares, drawn by the design system's site layer (QS370). What is left
// here is this product's half of it: which links, which words, and the grid the footer stands on.

// The mark carries its own silhouette, so it is sized by height rather than boxed as a tile.
const brand = { href: "/quickshell/", name: "Quickshell", logo: "/quickshell/logo.svg", logoShape: "mark" } as const;

const themeLabel = (next: string) => `Switch to ${next} theme`;

export function Nav() {
  return (
    <SiteNav
      brand={brand}
      parent={{ href: parentUrl, name: "alegauss", pre: "part of", title: "alegauss: small developer tools" }}
      links={navLinks}
      actions={
        <>
          <SiteButton href={releasesUrl}>{download.ctaShort}</SiteButton>
          {/* The repository keeps its place beside the download: it is what a reader checks
              before installing anything, and demoting it to a footer link would be the wrong
              half of this trade. */}
          <SiteButton variant="ghost" href={repoUrl}>
            ★ View on GitHub
          </SiteButton>
          <SiteThemeToggle storageKey={THEME_KEY} label={themeLabel} />
        </>
      }
    />
  );
}

export function Footer() {
  return (
    <SiteFooter
      brand={brand}
      links={footer.links}
      sponsor={
        <SiteSponsor
          declaration={sponsor}
          moreLabel="More at"
          note="Both Apache 2.0 and self-hostable."
        />
      }
      disclaimer={
        <>
          {footer.disclaimer} <ConsentSettings />
        </>
      }
    >
      <Cells className="cells--footer" />
    </SiteFooter>
  );
}

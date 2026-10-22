import { shell } from "../../lib/site-content";
import { SiteRich as Rich, SiteSectionHead } from "@viglet/viglet-design-system/site";

export function Shell() {
  return (
    <section id="shell">
      <div className="vg-site-wrap">
        <div className="reveal">
          <SiteSectionHead eyebrow={shell.eyebrow} title={shell.heading}>
            <Rich runs={shell.intro} />
          </SiteSectionHead>
        </div>
        <ul className="feat-list two reveal">
          {shell.points.map(([lead, rest]) => (
            <li key={lead}>
              <span className="chk">✓</span>
              <span>
                <b>{lead}</b>
                {rest}
              </span>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}

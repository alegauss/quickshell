import { session } from "../../lib/site-content";
import { sessionTerminal } from "../../lib/diagrams";
import { SiteRich as Rich, SiteSectionHead } from "@viglet/viglet-design-system/site";

// The connection, and what happens when it fails. The terminal beside the list is the point
// of the section: both of these are failures, and neither of them is an exception dialog.
export function Session() {
  return (
    <section id="session">
      <div className="vg-site-wrap">
        <div className="reveal">
          <SiteSectionHead eyebrow={session.eyebrow} title={session.heading}>
            <Rich runs={session.intro} />
          </SiteSectionHead>
        </div>
        <div className="split">
          <div className="split-txt reveal">
            <ul className="feat-list">
              {session.points.map(([lead, rest]) => (
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
          <figure className="reveal">
            <div className="term">
              <div className="bar">
                <i />
                <i />
                <i />
                <span>{session.terminalTitle}</span>
              </div>
              <pre dangerouslySetInnerHTML={{ __html: sessionTerminal }} />
            </div>
            <figcaption>
              <Rich runs={session.note} />
            </figcaption>
          </figure>
        </div>
      </div>
    </section>
  );
}

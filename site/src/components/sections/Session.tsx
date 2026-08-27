import { session } from "../../lib/site-content";
import { sessionTerminal } from "../../lib/diagrams";
import { Rich } from "../ui/Rich";

// The connection, and what happens when it fails. The terminal beside the list is the point
// of the section: both of these are failures, and neither of them is an exception dialog.
export function Session() {
  return (
    <section id="session">
      <div className="wrap">
        <div className="sec-head reveal">
          <div className="eyebrow">{session.eyebrow}</div>
          <h2>{session.heading}</h2>
          <p>
            <Rich runs={session.intro} />
          </p>
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

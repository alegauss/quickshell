import { terminal } from "../../lib/site-content";
import { protocolTerminal } from "../../lib/diagrams";
import { Rich } from "../ui/Rich";

// The emulator. Reversed against the section above it, so the two figures do not stack down
// one column of the page.
export function Terminal() {
  return (
    <section id="terminal">
      <div className="wrap">
        <div className="sec-head reveal">
          <div className="eyebrow">{terminal.eyebrow}</div>
          <h2>{terminal.heading}</h2>
          <p>
            <Rich runs={terminal.intro} />
          </p>
        </div>
        <div className="split rev">
          <div className="split-txt reveal">
            <ul className="feat-list">
              {terminal.points.map(([lead, rest]) => (
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
                <span>{terminal.terminalTitle}</span>
              </div>
              <pre dangerouslySetInnerHTML={{ __html: protocolTerminal }} />
            </div>
            <figcaption>
              <Rich runs={terminal.note} />
            </figcaption>
          </figure>
        </div>
      </div>
    </section>
  );
}

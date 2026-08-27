import { shell } from "../../lib/site-content";
import { Rich } from "../ui/Rich";

export function Shell() {
  return (
    <section id="shell">
      <div className="wrap">
        <div className="sec-head reveal">
          <div className="eyebrow">{shell.eyebrow}</div>
          <h2>{shell.heading}</h2>
          <p>
            <Rich runs={shell.intro} />
          </p>
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

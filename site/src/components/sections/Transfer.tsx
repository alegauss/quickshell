import { transfer } from "../../lib/site-content";
import { Rich } from "../ui/Rich";

// Files and ports in one section, because they are the same claim twice: a channel on the
// session you already authenticated, rather than a second connection wearing a second name.
export function Transfer() {
  return (
    <section id="transfer">
      <div className="wrap">
        <div className="sec-head reveal">
          <div className="eyebrow">{transfer.eyebrow}</div>
          <h2>{transfer.heading}</h2>
          <p>
            <Rich runs={transfer.intro} />
          </p>
        </div>
        <div className="split">
          {[transfer.files, transfer.ports].map((column) => (
            <div className="split-txt reveal" key={column.title}>
              <h2>{column.title}</h2>
              <ul className="feat-list">
                {column.items.map((item) => (
                  <li key={item.lead}>
                    <span className="chk">✓</span>
                    <span>
                      <b>{item.lead}</b>
                      <Rich runs={item.rest} />
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

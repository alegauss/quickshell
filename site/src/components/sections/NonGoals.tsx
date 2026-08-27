import { nonGoals } from "../../lib/site-content";
import { Rich } from "../ui/Rich";

export function NonGoals() {
  return (
    <section id="non-goals">
      <div className="wrap">
        <div className="sec-head reveal">
          <div className="eyebrow">{nonGoals.eyebrow}</div>
          <h2>{nonGoals.heading}</h2>
          <p>
            <Rich runs={nonGoals.intro} />
          </p>
        </div>
        <div className="nots reveal">
          {nonGoals.items.map((item) => (
            <div className="not" key={item.title}>
              <h4>
                <em>✗</em> {item.title}
              </h4>
              <p>{item.body}</p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

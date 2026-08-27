import { switching } from "../../lib/site-content";
import { Rich } from "../ui/Rich";

export function Switching() {
  return (
    <section id="switching">
      <div className="wrap">
        <div className="sec-head reveal">
          <div className="eyebrow">{switching.eyebrow}</div>
          <h2>{switching.heading}</h2>
          <p>
            <Rich runs={switching.intro} />
          </p>
        </div>
        <div className="steps">
          {switching.steps.map((step, i) => (
            <div className="step reveal" key={step.title}>
              <div className="n">{i + 1}</div>
              <h4>{step.title}</h4>
              <p>
                <Rich runs={step.body} />
              </p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

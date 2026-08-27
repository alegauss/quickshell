import { numbers } from "../../lib/site-content";
import { Rich } from "../ui/Rich";

// The performance budget, as six tiles. The figure is the headline and the method is the
// body, because a figure quoted without the run that produced it is not a measurement.
export function Numbers() {
  return (
    <section id="numbers">
      <div className="wrap">
        <div className="sec-head reveal">
          <div className="eyebrow">{numbers.eyebrow}</div>
          <h2>{numbers.heading}</h2>
          <p>
            <Rich runs={numbers.intro} />
          </p>
        </div>
        <div className="figures">
          {numbers.figures.map((figure) => (
            <div className="figure reveal" key={figure.what}>
              <div className="figure-what">{figure.what}</div>
              <div className="figure-n">
                {figure.value} <small>{figure.unit}</small>
              </div>
              <p>
                <Rich runs={figure.body} />
              </p>
            </div>
          ))}
        </div>
        <p className="figures-note reveal">
          <Rich runs={numbers.note} />
        </p>
      </div>
    </section>
  );
}

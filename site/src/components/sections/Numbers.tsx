import { numbers } from "../../lib/site-content";
import { SiteRich as Rich, SiteSectionHead } from "@viglet/viglet-design-system/site";

// The performance budget, as six tiles. The figure is the headline and the method is the
// body, because a figure quoted without the run that produced it is not a measurement.
export function Numbers() {
  return (
    <section id="numbers">
      <div className="vg-site-wrap">
        <div className="reveal">
          <SiteSectionHead eyebrow={numbers.eyebrow} title={numbers.heading}>
            <Rich runs={numbers.intro} />
          </SiteSectionHead>
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

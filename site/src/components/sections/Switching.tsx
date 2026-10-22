import { switching } from "../../lib/site-content";
import { SiteRich as Rich, SiteSectionHead } from "@viglet/viglet-design-system/site";

export function Switching() {
  return (
    <section id="switching">
      <div className="vg-site-wrap">
        <div className="reveal">
          <SiteSectionHead eyebrow={switching.eyebrow} title={switching.heading}>
            <Rich runs={switching.intro} />
          </SiteSectionHead>
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

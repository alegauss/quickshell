import { nonGoals } from "../../lib/site-content";
import { SiteRich as Rich, SiteSectionHead } from "@viglet/viglet-design-system/site";

export function NonGoals() {
  return (
    <section id="non-goals">
      <div className="vg-site-wrap">
        <div className="reveal">
          <SiteSectionHead eyebrow={nonGoals.eyebrow} title={nonGoals.heading}>
            <Rich runs={nonGoals.intro} />
          </SiteSectionHead>
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

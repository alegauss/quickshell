import { download, hero, heroTerminal, repoUrl } from "../../lib/site-content";
import { heroTerminal as heroTerminalHtml } from "../../lib/diagrams";
import { Rich } from "../ui/Rich";
import { Cells } from "../ui/Cells";

export function Hero() {
  return (
    <header className="hero" id="top">
      <div className="wrap">
        <img className="hero-icon" src="/quickshell/logo.svg" alt="Quickshell logo" />
        <div className="badge">
          <span className="dot" /> {hero.badge}
        </div>
        <h1>
          {hero.titleLead}
          <br />
          <span className="grad">{hero.titleAccent}</span>
        </h1>
        <p className="sub">
          <Rich runs={hero.sub} />
        </p>
        {/* The call to action is dropped from the Markdown twin by this attribute: it
            converts a reader and costs an agent the same forty words on every page. The
            first button scrolls to the section that says what the installer touches,
            because that is the question between a reader and an install. */}
        <div className="hero-cta" data-twin="omit">
          <a className="btn btn-primary" href="#download">
            {download.cta}
          </a>
          <a className="btn btn-ghost" href={repoUrl}>
            ★ View on GitHub
          </a>
        </div>

        <div className="hero-meta">
          {hero.meta.map((item) => (
            <span key={item}>{item}</span>
          ))}
        </div>

        <figure style={{ maxWidth: "820px", margin: "40px auto 0", textAlign: "left" }}>
          <div className="term">
            <div className="bar">
              <i />
              <i />
              <i />
              <span>{heroTerminal.title}</span>
            </div>
            <pre dangerouslySetInnerHTML={{ __html: heroTerminalHtml }} />
          </div>
          <figcaption>
            <Rich runs={heroTerminal.caption} />
          </figcaption>
        </figure>

        <div className="pills">
          {hero.pills.map((runs, i) => (
            <span className="pill" key={i}>
              <Rich runs={runs} />
            </span>
          ))}
        </div>
      </div>
      <Cells />
    </header>
  );
}

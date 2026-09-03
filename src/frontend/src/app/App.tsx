import prototypeDocument from '../../../../preview.html?raw';

/** Render the approved, fully mocked prototype without style leakage. */
export function App() {
  return <iframe className="prototype-frame" srcDoc={prototypeDocument} title="Unified Inbox" />;
}

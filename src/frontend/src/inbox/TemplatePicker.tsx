import { useState } from 'react';
import type { OutboundTemplateComponent, OutboundTemplateSelection } from '../api/inbox';
import type { WhatsAppTemplateInfo } from '../api/admin';

interface TemplatePickerProps {
  templates: WhatsAppTemplateInfo[] | null;
  loading: boolean;
  error: string;
  onCancel(): void;
  onConfirm(selection: OutboundTemplateSelection | null): void;
}

const parameterizedTypes = ['BODY', 'HEADER'];

export function TemplatePicker({ templates, loading, error, onCancel, onConfirm }: TemplatePickerProps) {
  const [picked, setPicked] = useState<WhatsAppTemplateInfo | null>(null);
  const [parameters, setParameters] = useState<Record<string, string>>({});

  const selectTemplate = (template: WhatsAppTemplateInfo | null) => {
    setPicked(template);
    if (!template) return;
    const initial: Record<string, string> = {};
    for (const component of template.components) {
      if (!parameterizedTypes.includes(component.type)) continue;
      for (let index = 0; index < component.parameterCount; index += 1) initial[`${component.type}:${index}`] = '';
    }
    setParameters(initial);
  };

  const confirm = () => {
    if (!picked) { onConfirm(null); return; }
    const components: OutboundTemplateComponent[] = picked.components
      .filter((component) => parameterizedTypes.includes(component.type) && component.parameterCount > 0)
      .map((component) => ({
        type: component.type,
        parameters: Array.from({ length: component.parameterCount }, (_, index) => ({ type: 'text', text: parameters[`${component.type}:${index}`] ?? '' })),
      }));
    onConfirm({ name: picked.name, language: picked.language, components });
    setPicked(null);
    setParameters({});
  };

  return <div className="template-sheet">
    <p role="status">Approved WhatsApp templates for this conversation.</p>
    {error && <p role="alert">{error}</p>}
    <label>Template<select aria-label="Approved template" value={picked ? `${picked.name} (${picked.language})` : ''} onChange={(event) => {
      const found = templates?.find((template) => `${template.name} (${template.language})` === event.target.value) ?? null;
      selectTemplate(found);
    }}>
      <option value="">Select a template…</option>
      {templates?.map((template) => <option key={`${template.name}:${template.language}`} value={`${template.name} (${template.language})`}>{template.name} ({template.language})</option>)}
    </select></label>
    {loading && <p role="status">Loading templates…</p>}
    {!loading && templates?.length === 0 && <p>No approved templates are available. Refresh to try again.</p>}
    {picked && picked.components.filter((component) => parameterizedTypes.includes(component.type) && component.parameterCount > 0).map((component) => (
      <fieldset key={component.type}><legend>{component.type}</legend>
        {Array.from({ length: component.parameterCount }, (_, index) => (
          <label key={index}>{component.type.toLowerCase()} parameter {index + 1}
            <input aria-label={`${component.type} parameter ${index + 1}`} value={parameters[`${component.type}:${index}`] ?? ''} onChange={(event) => setParameters({ ...parameters, [`${component.type}:${index}`]: event.target.value })} />
          </label>
        ))}
      </fieldset>
    ))}
    <div>
      <button type="button" aria-label="Confirm template" disabled={!picked} onClick={confirm}>Use selected template</button>
      <button type="button" onClick={onCancel}>Cancel</button>
    </div>
  </div>;
}

export function accessMode(value = process.env.REVIT_MCP_ACCESS ?? 'write') {
  if (!['read', 'write'].includes(value)) throw new Error('REVIT_MCP_ACCESS must be read or write.');
  return value;
}

export function isModelWriteTool(name) {
  return /^(preview_|apply_)/.test(name)
    || ['get_schedulable_fields', 'validate_family_file', 'export_model'].includes(name);
}

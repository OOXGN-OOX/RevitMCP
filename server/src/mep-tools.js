import * as z from 'zod/v4';

const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const coordinate = z.number().finite().min(-100000).max(100000);
const point = z.object({ xMeters: coordinate, yMeters: coordinate, zMeters: coordinate }).strict();
const dimension = z.number().finite().min(0.001).max(3);
const write = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };
const connector = z.object({ elementId: id, connectorId: z.number().int().min(0).max(2147483647) }).strict();
const connection = z.object({
  first: connector,
  second: connector,
  fitting: z.enum(['direct', 'elbow', 'union', 'transition'])
}).strict().superRefine((value, ctx) => {
  if (value.first.elementId === value.second.elementId)
    ctx.addIssue({ code: 'custom', message: 'Connectors must belong to different elements.' });
});
const segment = z.object({
  kind: z.enum(['pipe', 'duct']),
  typeId: id,
  systemTypeId: id,
  levelId: id,
  start: point,
  end: point,
  diameterMeters: dimension.optional(),
  widthMeters: dimension.optional(),
  heightMeters: dimension.optional()
}).strict().superRefine((value, ctx) => {
  const diameter = value.diameterMeters !== undefined;
  const rectangle = value.widthMeters !== undefined && value.heightMeters !== undefined;
  if (value.kind === 'pipe' ? !diameter || value.widthMeters !== undefined || value.heightMeters !== undefined : diameter === rectangle || (!diameter && !rectangle)) {
    ctx.addIssue({ code: 'custom', message: 'Pipe needs diameter; duct needs either diameter or both width and height.' });
  }
  const length = Math.hypot(value.end.xMeters - value.start.xMeters,
    value.end.yMeters - value.start.yMeters, value.end.zMeters - value.start.zMeters);
  if (length < 0.01 || length > 300)
    ctx.addIssue({ code: 'custom', message: 'Segment length must be between 0.01 and 300 m.' });
});

export function registerMepTools(register) {
  register('list_mep_connectors', 'Прочитать физические коннекторы труб, воздуховодов, фитингов и оборудования: ID, положение в метрах, направление, размеры и соединённые элементы. Перед соединением используйте ID из этого ответа.',
    z.object({ elementIds: z.array(id).min(1).max(50).refine(values => new Set(values).size === values.length, 'Element IDs must be unique.'), unusedOnly: z.boolean().default(false) }).strict());
  register('preview_connect_mep', 'Проверить соединение двух свободных коннекторов ОВ/ВК с откатом. Явно выберите direct (совпадающие соосные концы), elbow (отвод), union (муфта) или transition (переход). Фитинги выбираются Revit по настройкам трассировки; геометрия прилегающих участков может измениться.', connection, write);
  register('apply_connect_mep', 'Применить проверенное соединение MEP-коннекторов одной транзакцией.',
    z.object({ planId: z.string().min(1).max(80) }).strict(), write);
  register('list_mep_types', 'Перечислить доступные типы труб, воздуховодов, систем и уровни открытого проекта.', z.object({}).strict());
  register('preview_create_mep', 'Проверить создание прямых труб и воздуховодов Revit с откатом. Размеры и координаты в метрах. Соединения и фитинги не создаются автоматически.',
    z.object({ segments: z.array(segment).min(1).max(50) }).strict(), write);
  register('apply_create_mep', 'Создать проверенный пакет прямых труб и воздуховодов одной транзакцией.',
    z.object({ planId: z.string().min(1).max(80) }).strict(), write);
}

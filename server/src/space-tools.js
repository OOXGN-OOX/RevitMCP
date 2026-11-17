import * as z from 'zod/v4';
const id=z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const meters=z.number().finite().min(-1000).max(1000);
const write={readOnlyHint:false,destructiveHint:false,openWorldHint:false};
export function registerSpaceTools(register){
  register('list_revit_links','Прочитать непосредственные экземпляры RVT-связей: ID/UniqueId экземпляра и типа, документ, доступность, Room Bounding типа, соответствие стадий host → link, уровни и GetTransform в основной проект. Выгруженные связи явно недоступны. Начало преобразования и отметки — в метрах; базис безразмерный.',z.object({}).strict());
  register('list_spaces','Прочитать MEP Spaces основного проекта: источник Room по UniqueId связи и помещения, отдельные номер и имя, стадия, уровень, площадь, объём, точка и вертикальные ограничения. Координаты — внутренние координаты основного проекта, в метрах.',z.object({phaseId:id.optional(),levelId:id.optional(),offset:z.number().int().min(0).default(0),limit:z.number().int().min(1).max(500).default(100)}).strict());
  register('preview_create_spaces_from_link','Пробное создание MEP Spaces в основном проекте по явным Room загруженной связи. Проверяет стадии, уровни, точки, контуры и площади после commit, затем полностью откатывает. verticalExtents содержит для каждого roomId lowerOffsetMeters от hostLevel, upperLevelId основного проекта и upperOffsetMeters. enableRoomBounding=true разрешает включить свойство типа связи для ВСЕХ его экземпляров. Сопоставление хранится по UniqueId экземпляра связи + Room. Обновление затрагивает только имя/номер. planCreated=false при ошибках; apply допустим только для полного действующего плана.',z.object({
    linkInstanceId:id,roomIds:z.array(id).min(1).max(100).refine(a=>new Set(a).size===a.length),hostPhaseId:id,
    levelMap:z.array(z.object({sourceLevelId:id,hostLevelId:id}).strict()).min(1).max(100).refine(a=>new Set(a.map(v=>v.sourceLevelId)).size===a.length),
    verticalExtents:z.array(z.object({roomId:id,lowerOffsetMeters:meters,upperLevelId:id,upperOffsetMeters:meters}).strict()).min(1).max(100).refine(a=>new Set(a.map(v=>v.roomId)).size===a.length),
    enableRoomBounding:z.boolean().default(false),copyNames:z.boolean().default(true),copyNumbers:z.boolean().default(true),
    existingPolicy:z.enum(['skip','updateNamesAndNumbers','error']).default('skip')
  }).strict().refine(a=>a.roomIds.length===a.verticalExtents.length&&a.verticalExtents.every(v=>a.roomIds.includes(v.roomId)),{message:'Provide vertical extents for every requested room, with no extras.'}),write);
  register('apply_create_spaces_from_link','Атомарно применить проверенный план MEP Spaces. Повторно проверяет основной проект, содержимое связанного документа и преобразование связи. При изменениях требуется новый preview; действует стандартное подтверждение и autoConfirm.',z.object({planId:z.string().min(1).max(80)}).strict(),write);
}

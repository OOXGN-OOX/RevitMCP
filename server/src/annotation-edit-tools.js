import * as z from 'zod/v4';
import {annotationOperations,refineAnnotation} from './family-annotation-operations.js';
const id=z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const point=z.object({xMeters:z.number().finite().min(-100000).max(100000),yMeters:z.number().finite().min(-100000).max(100000),zMeters:z.number().finite().min(-100000).max(100000)}).strict();
const plan=z.object({planId:z.string().min(1).max(80)}).strict();
const write={readOnlyHint:false,destructiveHint:false,openWorldHint:false};
const tagEdit=z.object({tagId:id,headPosition:point.optional(),orientation:z.enum(['Horizontal','Vertical','AnyModelDirection']).optional(),rotationDegrees:z.number().finite().min(-360).max(360).optional(),hasLeader:z.boolean().optional(),leaderEndCondition:z.enum(['Attached','Free']).optional(),
  leaders:z.array(z.object({referenceIndex:z.number().int().min(0).max(99),elbowPosition:point.optional(),endPosition:point.optional(),visible:z.boolean().optional()}).strict()).max(100).optional(),
  addLocalElementIds:z.array(id).min(1).max(100).optional(),removeReferenceIndices:z.array(z.number().int().min(0).max(99)).min(1).max(100).optional()
}).strict().refine(v=>Object.keys(v).some(k=>k!=='tagId'),'Specify a change.')
 .refine(v=>!v.leaders||new Set(v.leaders.map(l=>l.referenceIndex)).size===v.leaders.length,'Duplicate leader reference indices.')
 .refine(v=>!v.addLocalElementIds||new Set(v.addLocalElementIds).size===v.addLocalElementIds.length,'Duplicate added element IDs.')
 .refine(v=>!v.removeReferenceIndices||new Set(v.removeReferenceIndices).size===v.removeReferenceIndices.length,'Duplicate removed reference indices.');
export function registerAnnotationEditTools(register){
  register('read_tags','Прочитать существующие IndependentTag: голову, ориентацию, текст, привязки по стабильным ссылкам, изгибы, концы и видимость каждой выноски. referenceIndex используется только в плане той же ревизии документа.',z.object({tagIds:z.array(id).min(1).max(100)}).strict());
  register('preview_edit_tags','Изменить существующие IndependentTag с откатом: голову, ориентацию, выноски и локальные привязки. RoomTag/SpaceTag и связи не поддерживаются. Новые привязки добавляются перед удалением старых; ожидаемое множество ссылок проверяется после commit.',z.object({documentRevision:z.number().int().min(0).optional(),tags:z.array(tagEdit).min(1).max(100).refine(a=>new Set(a.map(v=>v.tagId)).size===a.length,'Duplicate tag IDs.')} ).strict(),write);
  register('apply_edit_tags','Повторить редактирование марок по проверенному planId; сверить итоговые ссылки после commit.',plan,write);
  register('get_view_annotation_resources','Прочитать виды, стили текста/линий, типы областей и Generic Annotation для создания 2D-оформления.',z.object({}).strict());
  register('read_view_annotations','Прочитать текст, кривые, области и размеры заданного вида с явными ID, параметрами и геометрией. Выдача постраничная, ошибки чтения отражаются отдельно.',z.object({viewId:id,offset:z.number().int().min(0).default(0),limit:z.number().int().min(1).max(500).default(100)}).strict());
  const projectOps=annotationOperations.filter(s=>!['annotationDimension','setAnnotationDimension'].includes(s.shape.action.value));
  projectOps.push(z.object({action:z.literal('placeGenericAnnotation'),key:z.string().min(1).max(200),viewId:id,typeId:id,position:z.object({xMm:z.number().finite().min(-100000).max(100000),yMm:z.number().finite().min(-100000).max(100000)}).strict(),rotationDegrees:z.number().finite().min(-360).max(360).default(0)}).strict());
  register('preview_edit_view_annotations','Создать/изменить постоянный текст, линии/дуги, области или Generic Annotation в заданных видах. Позиции в миллиметрах на бумаге вдоль Right/Up вида от его Origin. Символические линии и семейные размеры требуют build_family. Используются схемы каталога build_family для annotation* операций.',z.object({operations:z.array(z.discriminatedUnion('action',projectOps).superRefine(refineAnnotation)).min(1).max(200)}).strict(),write);
  register('apply_edit_view_annotations','Применить проверенные операции 2D-оформления видов одной группой транзакций.',plan,write);
  const parameter=z.object({elementId:id,parameterName:z.string().min(1).max(200),parameterScope:z.enum(['instance','type']).default('instance'),value:z.string().max(16000)}).strict();
  register('validate_annotation_family','Загрузить RFA в скрытый отдельный образец RVT, поставить марки на заданные элементы, проверить текст/геометрию и экспортировать PNG при заданных масштабах и значениях. Оба исходных файла не сохраняются; RVT должен быть закрыт, без совместной работы. Выходная папка создаётся внутри directory.',z.object({path:z.string().min(4).max(240),sampleProjectPath:z.string().min(4).max(240),directory:z.string().min(3).max(170),samples:z.array(z.object({viewId:id,scale:z.number().int().min(1).max(1000),typeName:z.string().min(1).max(200),elementIds:z.array(id).min(1).max(50),parameters:z.array(parameter).max(500).default([])}).strict()).min(1).max(20),pixelSize:z.number().int().min(500).max(4000).default(1600)}).strict(),write);
}

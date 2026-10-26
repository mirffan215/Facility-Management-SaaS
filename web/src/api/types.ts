// Shapes returned by the FMS API (camelCase JSON, enums as strings). Keep in step with docs/phase-1-summary.md.

export type LocationType = 'Site' | 'Building' | 'Floor' | 'Space';
export type AssetStatus = 'Planned' | 'Active' | 'UnderMaintenance' | 'OutOfService' | 'Retired';
export type Criticality = 'Low' | 'Medium' | 'High' | 'Critical';
export type WarrantyState = 'None' | 'Active' | 'ExpiringSoon' | 'Expired';
export type MeterKind = 'Cumulative' | 'Gauge';
export type ReadingSource = 'Manual' | 'Mobile' | 'Import' | 'Device';
export type DocumentKind = 'Manual' | 'Warranty' | 'Certificate' | 'Photo' | 'Drawing' | 'Other';
export type CustomFieldTarget = 'Location' | 'Asset';
export type CustomFieldType = 'Text' | 'Number' | 'Date' | 'Boolean' | 'Choice';

export type CustomFieldValues = Record<string, string | number | boolean>;

export interface Page<T> {
  items: T[];
  total: number;
  pageNumber: number;
  pageSize: number;
}

export interface Me {
  subject: string | null;
  provider: string | null;
  tenantId: string | null;
  userId: string | null;
  permissions: string[] | null;
}

export interface ProviderInfo {
  name: string;
  authority: string;
  clientId: string | null;
  scope: string | null;
}

export interface Location {
  id: string;
  parentId: string | null;
  type: LocationType;
  code: string;
  name: string;
  nameAr: string | null;
  address: string | null;
  timeZone: string | null;
  areaSquareMetres: number | null;
  customFields: CustomFieldValues;
  isArchived: boolean;
  rowVersion: string;
}

export interface LocationNode {
  id: string;
  parentId: string | null;
  type: LocationType;
  code: string;
  name: string;
  nameAr: string | null;
}

export interface AssetClass {
  id: string;
  parentId: string | null;
  code: string;
  name: string;
  nameAr: string | null;
  isArchived: boolean;
  rowVersion: string;
}

export interface Asset {
  id: string;
  code: string;
  name: string;
  nameAr: string | null;
  assetClassId: string;
  locationId: string;
  parentAssetId: string | null;
  status: AssetStatus;
  statusReason: string | null;
  criticality: Criticality;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  installDate: string | null;
  warrantyExpiry: string | null;
  warranty: WarrantyState;
  tagCode: string;
  customFields: CustomFieldValues;
  retiredAt: string | null;
  rowVersion: string;
}

export interface CustomFieldDefinition {
  id: string;
  target: CustomFieldTarget;
  key: string;
  labelEn: string;
  labelAr: string | null;
  dataType: CustomFieldType;
  isRequired: boolean;
  choices: string[];
  assetClassId: string | null;
  sortOrder: number;
  isArchived: boolean;
}

export interface Meter {
  id: string;
  assetId: string;
  name: string;
  nameAr: string | null;
  unit: string;
  kind: MeterKind;
  minValue: number | null;
  maxValue: number | null;
  isArchived: boolean;
  lastValue: number | null;
  lastReadAt: string | null;
  rowVersion: string;
}

export interface MeterReading {
  id: string;
  meterId: string;
  value: number;
  readAt: string;
  recordedAt: string;
  recordedBy: string | null;
  source: ReadingSource;
  note: string | null;
  isReset: boolean;
  clientId: string | null;
  isVoided: boolean;
}

export interface AssetDocument {
  id: string;
  assetId: string;
  kind: DocumentKind;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  sha256: string;
  description: string | null;
  uploadedAt: string;
  uploadedBy: string | null;
  isDeleted: boolean;
}

export interface ImportRowResult {
  line: number;
  outcome: 'Created' | 'Error';
  code: string | null;
  errors: string[];
}

export interface ImportReport {
  dryRun: boolean;
  committed: boolean;
  total: number;
  created: number;
  failed: number;
  rows: ImportRowResult[];
}

export interface UserSummary {
  id: string;
  email: string | null;
  displayName: string | null;
  isActive: boolean;
  roleIds: string[];
  provider: string | null;
  lastSignInAt: string | null;
}

export interface RoleSummary {
  id: string;
  name: string;
  isSystem: boolean;
  permissions: string[];
}

export type WorkOrderType = 'Corrective' | 'Emergency' | 'Planned';
export type WorkPriority = 'Low' | 'Medium' | 'High' | 'Urgent';
export type WorkOrderStatus = 'Requested' | 'Approved' | 'Assigned' | 'InProgress' | 'OnHold' | 'Completed' | 'Verified' | 'Closed' | 'Cancelled';
export type WorkOrderAction =
  | 'Created' | 'Approved' | 'Rejected' | 'Assigned' | 'Started' | 'Held' | 'Resumed' | 'Completed' | 'Verified' | 'Closed' | 'Reopened' | 'Cancelled' | 'Edited';
export type ClosureCode = 'Fixed' | 'NoFaultFound' | 'TemporaryFix' | 'PartsRequired' | 'Duplicate' | 'NotRequired';

export interface WorkOrder {
  id: string;
  number: number;
  displayNumber: string;
  title: string;
  description: string | null;
  type: WorkOrderType;
  priority: WorkPriority;
  status: WorkOrderStatus;
  locationId: string;
  locationName: string | null;
  assetId: string | null;
  assetName: string | null;
  categoryId: string | null;
  categoryName: string | null;
  requestedBy: string;
  requestedByName: string | null;
  requestedAt: string;
  assignedTo: string | null;
  assignedToName: string | null;
  dueAt: string | null;
  isOverdue: boolean;
  startedAt: string | null;
  completedAt: string | null;
  verifiedAt: string | null;
  closedAt: string | null;
  statusReason: string | null;
  closureCode: ClosureCode | null;
  closureNotes: string | null;
  /** What this person may do right now; the server decides. */
  allowed: WorkOrderAction[];
  rowVersion: string;
  sla: Sla;
}

export interface WorkOrderEvent {
  id: string;
  action: WorkOrderAction;
  fromStatus: WorkOrderStatus | null;
  toStatus: WorkOrderStatus;
  actorId: string | null;
  actorName: string | null;
  at: string;
  note: string | null;
  subjectId: string | null;
  subjectName: string | null;
}

export interface WorkOrderComment {
  id: string;
  authorId: string;
  authorName: string | null;
  at: string;
  body: string;
  isInternal: boolean;
}

export interface WorkOrderDetail {
  order: WorkOrder;
  events: WorkOrderEvent[];
  comments: WorkOrderComment[];
}

export interface WorkCategory {
  id: string;
  code: string;
  name: string;
  nameAr: string | null;
  defaultPriority: WorkPriority;
  isArchived: boolean;
  rowVersion: string;
}

export interface WorkSettings {
  requireApproval: boolean;
  requireVerification: boolean;
  rowVersion: string | null;
}

export interface Assignee {
  id: string;
  displayName: string | null;
  email: string | null;
}

export interface AssetChoice {
  id: string;
  code: string;
  name: string;
  locationId: string | null;
}

export type SlaState = 'None' | 'OnTrack' | 'AtRisk' | 'Paused' | 'Met' | 'Breached';

export interface Sla {
  state: SlaState;
  responseDueAt: string | null;
  resolutionDueAt: string | null;
  respondedAt: string | null;
  responseBreached: boolean;
  paused: boolean;
}

export interface NotificationItem {
  id: string;
  type: string;
  data: { workOrderId?: string; number?: string; title?: string; reason?: string | null };
  linkPath: string | null;
  createdAt: string;
  isRead: boolean;
}

export interface DayHours {
  start: string | null;
  end: string | null;
}

export interface Holiday {
  date: string;
  name: string;
}

export interface WorkCalendar {
  id: string;
  name: string;
  timeZoneId: string;
  hours: Record<string, DayHours | null>;
  isDefault: boolean;
  isArchived: boolean;
  holidays: Holiday[];
  siteIds: string[];
  rowVersion: string;
}

export interface SlaTarget {
  priority: WorkPriority;
  responseMinutes: number;
  resolutionMinutes: number;
}

export interface SlaPolicy {
  id: string;
  name: string;
  categoryId: string | null;
  categoryName: string | null;
  isDefault: boolean;
  isArchived: boolean;
  targets: SlaTarget[];
  rowVersion: string;
}

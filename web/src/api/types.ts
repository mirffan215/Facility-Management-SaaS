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
  currencyCode: string;
  labourRatePerHour: number;
  requireCompletionSignature: boolean;
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

export type ChecklistResult = 'Pending' | 'Done' | 'Failed' | 'NotApplicable';
export type CostKind = 'Material' | 'Contractor' | 'Other';
export type AttachmentKind = 'Before' | 'After' | 'Other';
export type SignatureRole = 'Technician' | 'Customer';

export interface ChecklistItem {
  id: string;
  position: number;
  text: string;
  required: boolean;
  result: ChecklistResult;
  note: string | null;
  resultByName: string | null;
  resultAt: string | null;
}

export interface ChecklistTemplate {
  id: string;
  name: string;
  nameAr: string | null;
  isArchived: boolean;
  items: { text: string; required: boolean }[];
  rowVersion: string;
}

export interface TimeEntry {
  id: string;
  workerId: string;
  workerName: string | null;
  workedOn: string;
  minutes: number;
  hourlyRate: number;
  labourCost: number;
  note: string | null;
  recordedAt: string;
  isVoided: boolean;
  voidReason: string | null;
}

export interface CostLine {
  id: string;
  kind: CostKind;
  description: string;
  reference: string | null;
  quantity: number;
  unitCost: number;
  total: number;
  recordedByName: string | null;
  recordedAt: string;
  isVoided: boolean;
  voidReason: string | null;
}

export interface CostSummary {
  currencyCode: string;
  minutes: number;
  labourCost: number;
  material: number;
  contractor: number;
  other: number;
  total: number;
}

export interface WorkSignature {
  id: string;
  role: SignatureRole;
  signerName: string;
  signedAt: string;
  recordedByName: string | null;
}

export interface WorkAttachment {
  id: string;
  kind: AttachmentKind;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  description: string | null;
  uploadedAt: string;
  uploadedByName: string | null;
  uploadedBy: string;
}

// ---------------- preventive maintenance ----------------

export type PmTargetKind = 'Asset' | 'AssetClass';
export type PmTimeBasis = 'None' | 'Fixed' | 'Floating';
export type PmIntervalUnit = 'Day' | 'Week' | 'Month' | 'Year';
export type ForecastKind = 'Open' | 'Projected' | 'MeterEstimate';

export interface PmTemplate {
  id: string;
  name: string;
  nameAr: string | null;
  instructions: string | null;
  priority: WorkPriority;
  estimatedMinutes: number | null;
  categoryId: string | null;
  checklistTemplateId: string | null;
  checklistName: string | null;
  isArchived: boolean;
  rowVersion: string;
}

export interface PmSchedule {
  id: string;
  name: string;
  templateId: string;
  templateName: string | null;
  targetKind: PmTargetKind;
  assetId: string | null;
  assetName: string | null;
  assetClassId: string | null;
  assetClassName: string | null;
  timeBasis: PmTimeBasis;
  intervalUnit: PmIntervalUnit;
  interval: number;
  startOn: string;
  leadDays: number;
  meterName: string | null;
  meterInterval: number | null;
  assignToUserId: string | null;
  assignToName: string | null;
  isArchived: boolean;
  assets: number;
  open: number;
  rowVersion: string;
}

export interface ForecastItem {
  date: string;
  kind: ForecastKind;
  scheduleId: string;
  scheduleName: string;
  templateName: string | null;
  assetId: string;
  assetCode: string | null;
  assetName: string | null;
  locationId: string | null;
  assetClassId: string | null;
  workOrderId: string | null;
  workOrderNumber: string | null;
  status: WorkOrderStatus | null;
  overdue: boolean;
}

export interface Forecast {
  from: string;
  to: string;
  today: string;
  items: ForecastItem[];
  truncated: boolean;
}

export interface ComplianceCounts {
  due: number;
  onTime: number;
  late: number;
  skipped: number;
  overdue: number;
  upcoming: number;
  onTimeRate: number | null;
}

export interface Compliance {
  from: string;
  to: string;
  today: string;
  total: ComplianceCounts;
  months: { month: string; counts: ComplianceCounts }[];
  byTemplate: { id: string | null; name: string; counts: ComplianceCounts }[];
  byAssetClass: { id: string | null; name: string; counts: ComplianceCounts }[];
  plannedWorkOrders: number;
  otherWorkOrders: number;
  plannedShare: number | null;
}

// ---------------- inventory ----------------

export type MovementType = 'Receipt' | 'Issue' | 'Return' | 'TransferOut' | 'TransferIn' | 'Adjustment' | 'CountAdjustment';
export type StockTakeStatus = 'Counting' | 'Submitted' | 'Approved' | 'Cancelled';

export interface Store {
  id: string;
  code: string;
  name: string;
  nameAr: string | null;
  siteId: string | null;
  isArchived: boolean;
  rowVersion: string;
}

export interface Item {
  id: string;
  code: string;
  name: string;
  nameAr: string | null;
  description: string | null;
  unit: string;
  barcode: string | null;
  isArchived: boolean;
  onHand: number;
  available: number;
  rowVersion: string;
}

export interface StockLevel {
  storeId: string;
  storeCode: string;
  storeName: string;
  itemId: string;
  itemCode: string;
  itemName: string;
  unit: string;
  onHand: number;
  reserved: number;
  available: number;
  averageCost: number;
  value: number;
  minQty: number | null;
  maxQty: number | null;
  isLow: boolean;
  suggestedOrder: number;
  rowVersion: string;
}

export interface Movement {
  id: string;
  storeId: string;
  storeName: string | null;
  itemId: string;
  itemCode: string | null;
  itemName: string | null;
  type: MovementType;
  quantity: number;
  unitCost: number;
  balanceAfter: number;
  at: string;
  by: string | null;
  byName: string | null;
  workOrderId: string | null;
  reference: string | null;
  reason: string | null;
}

export interface WorkPart {
  itemId: string;
  itemCode: string;
  itemName: string;
  unit: string;
  storeId: string;
  storeName: string;
  reserved: number;
  issued: number;
  unitCost: number;
  cost: number;
  storeAvailable: number;
}

export interface WorkParts {
  workOrderId: string;
  acceptsParts: boolean;
  lines: WorkPart[];
  totalCost: number;
}

export interface PmTemplatePart {
  itemId: string;
  itemCode: string;
  itemName: string;
  unit: string;
  quantity: number;
  storeId: string | null;
  storeName: string | null;
}

export interface StockTakeLine {
  itemId: string;
  itemCode: string;
  itemName: string;
  unit: string;
  systemQty: number;
  countedQty: number | null;
  variance: number | null;
  unitCost: number;
  varianceValue: number | null;
}

export interface StockTake {
  id: string;
  storeId: string;
  storeName: string;
  status: StockTakeStatus;
  note: string | null;
  startedAt: string;
  startedByName: string | null;
  submittedAt: string | null;
  submittedByName: string | null;
  decidedAt: string | null;
  decidedByName: string | null;
  lines: number;
  counted: number;
  differences: number;
  netVarianceValue: number;
  detail: StockTakeLine[] | null;
  rowVersion: string;
}

// ---------------- vendors and contracts ----------------

export type VendorStatus = 'Active' | 'OnHold' | 'Blocked';
export type ContractType = 'Amc' | 'Sla' | 'Service' | 'Supply' | 'Other';
export type ContractStatus = 'Upcoming' | 'Active' | 'Expiring' | 'Expired' | 'Terminated';
export type CoverageKind = 'Site' | 'AssetClass' | 'Asset';
export type ContractDocumentKind = 'Contract' | 'Amendment' | 'Certificate' | 'Insurance' | 'Other';

export interface Vendor {
  id: string;
  code: string;
  name: string;
  nameAr: string | null;
  status: VendorStatus;
  statusReason: string | null;
  taxNumber: string | null;
  registrationNumber: string | null;
  email: string | null;
  phone: string | null;
  address: string | null;
  country: string | null;
  paymentTerms: string | null;
  notes: string | null;
  erpAccount: string | null;
  isArchived: boolean;
  activeContracts: number;
  rowVersion: string;
}

export interface VendorContact {
  id: string;
  name: string;
  role: string | null;
  email: string | null;
  phone: string | null;
  isPrimary: boolean;
}

export interface ContractSla {
  priority: number;
  responseMinutes: number;
  resolutionMinutes: number;
}

export interface Contract {
  id: string;
  vendorId: string;
  vendorName: string | null;
  number: string;
  title: string;
  type: ContractType;
  startOn: string;
  endOn: string;
  value: number | null;
  currencyCode: string;
  renewalNoticeDays: number;
  notes: string | null;
  status: ContractStatus;
  daysLeft: number;
  terminatedAt: string | null;
  terminationReason: string | null;
  coverage: { kind: CoverageKind; targetId: string; name: string | null }[];
  sla: ContractSla[];
  documents: number;
  rowVersion: string;
}

export interface ContractDocument {
  id: string;
  contractId: string;
  kind: ContractDocumentKind;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  description: string | null;
  uploadedAt: string;
}

export interface VendorAssignment {
  vendorId: string;
  vendorName: string;
  vendorNameAr: string | null;
  contractId: string | null;
  contractNumber: string | null;
  contractTitle: string | null;
  contractInForce: boolean;
  covered: boolean | null;
  responseMinutes: number | null;
  resolutionMinutes: number | null;
}

// ---------------- purchasing ----------------

export type RequestStatus = 'Draft' | 'Submitted' | 'Approved' | 'Rejected' | 'Cancelled' | 'Ordered';
export type OrderStatus = 'Issued' | 'Acknowledged' | 'PartiallyReceived' | 'Received' | 'Cancelled';

export interface PurchasingSettings {
  level1Limit: number;
  level2Limit: number;
  overrunTolerancePercent: number;
  currencyCode: string;
  rowVersion: string | null;
}

export interface RequestLine {
  id: string;
  position: number;
  itemId: string | null;
  itemCode: string | null;
  storeId: string | null;
  storeName: string | null;
  description: string;
  unit: string;
  quantity: number;
  estimatedUnitPrice: number;
  total: number;
}

export interface PurchaseRequest {
  id: string;
  number: number;
  display: string;
  title: string;
  justification: string | null;
  status: RequestStatus;
  requestedBy: string;
  requestedByName: string | null;
  requestedAt: string;
  preferredVendorId: string | null;
  preferredVendorName: string | null;
  workOrderId: string | null;
  total: number;
  requiredLevel: number;
  submittedAt: string | null;
  decidedByName: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  orderId: string | null;
  orderDisplay: string | null;
  canApprove: boolean;
  lines: RequestLine[] | null;
  rowVersion: string;
}

export interface OrderLine {
  id: string;
  position: number;
  itemId: string | null;
  itemCode: string | null;
  storeId: string | null;
  storeName: string | null;
  description: string;
  unit: string;
  quantity: number;
  unitPrice: number;
  total: number;
  receivedQty: number;
  outstanding: number;
}

export interface PurchaseOrder {
  id: string;
  number: number;
  display: string;
  requestId: string;
  requestDisplay: string | null;
  vendorId: string;
  vendorCode: string;
  vendorName: string;
  status: OrderStatus;
  issuedByName: string | null;
  issuedAt: string;
  expectedOn: string | null;
  currencyCode: string;
  total: number;
  notes: string | null;
  acknowledgedAt: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  erpReference: string | null;
  lines: OrderLine[] | null;
  receipts: { id: string; lineId: string; quantity: number; receivedAt: string; receivedByName: string | null; deliveryNote: string | null }[] | null;
  rowVersion: string;
}

// ---------------- accounting integration ----------------

export type ErpEntity = 'Vendor' | 'PurchaseOrder' | 'GoodsReceipt';
export type ExportStatus = 'Pending' | 'Sending' | 'Succeeded' | 'DeadLetter' | 'Superseded';
export type ReconcileState = 'NotChecked' | 'Matched' | 'Mismatch' | 'Missing';

export interface ErpSettings {
  enabled: boolean;
  adapter: string;
  companyId: string;
  availableAdapters: string[];
  rowVersion: string | null;
}

export interface ErpSummary {
  enabled: boolean;
  pending: number;
  sending: number;
  succeeded: number;
  deadLetter: number;
  superseded: number;
  mismatched: number;
  oldestPendingAt: string | null;
}

export interface ErpItem {
  id: string;
  entity: ErpEntity;
  entityId: string;
  label: string | null;
  revision: number;
  status: ExportStatus;
  attempts: number;
  nextAttemptAt: string;
  lastError: string | null;
  erpReference: string | null;
  createdAt: string;
  succeededAt: string | null;
  reconcile: ReconcileState;
  reconciledAt: string | null;
  reconcileDetail: string | null;
}

// ---------------- vendor portal ----------------

export interface PortalMe {
  vendorId: string;
  vendorName: string;
  vendorNameAr: string | null;
  status: VendorStatus;
}

export interface PortalContract {
  id: string;
  number: string;
  title: string;
  type: ContractType;
  startOn: string;
  endOn: string;
  status: ContractStatus;
  daysLeft: number;
  value: number | null;
  currencyCode: string;
  coverage: { kind: CoverageKind; targetId: string; name: string | null }[];
  sla: ContractSla[];
  documents: number;
}

export interface PortalOrder {
  id: string;
  display: string;
  status: OrderStatus;
  issuedAt: string;
  expectedOn: string | null;
  currencyCode: string;
  total: number;
  notes: string | null;
  acknowledgedAt: string | null;
  cancelReason: string | null;
  lines: { position: number; description: string; unit: string; quantity: number; unitPrice: number; total: number; receivedQty: number }[] | null;
}

export interface PortalWorkComment {
  id: string;
  authorName: string | null;
  at: string;
  body: string;
  mine: boolean;
}

export interface PortalWork {
  id: string;
  display: string;
  title: string;
  description: string | null;
  priority: WorkPriority;
  status: WorkOrderStatus;
  locationName: string | null;
  assetName: string | null;
  dueAt: string | null;
  contractNumber: string | null;
  responseMinutes: number | null;
  resolutionMinutes: number | null;
  comments: PortalWorkComment[] | null;
}

export interface PortalInvitation {
  id: string;
  email: string;
  createdAt: string;
  expiresAt: string;
  acceptedAt: string | null;
  revokedAt: string | null;
  state: string;
}

export interface PortalAccess {
  users: { userId: string; name: string | null; email: string | null; isActive: boolean; linkedAt: string }[];
  invitations: PortalInvitation[];
}

// ---------------- spaces ----------------

export type SpaceKind = 'Office' | 'Meeting' | 'Desk' | 'Parking' | 'Storage' | 'Common' | 'Other';
export type PinKind = 'Space' | 'Asset';
export type OccupantKind = 'Person' | 'Team';
export type AllocationState = 'Upcoming' | 'Current' | 'Ended' | 'Cancelled';

export interface SpaceInfo {
  locationId: string;
  code: string;
  name: string;
  kind: SpaceKind | null;
  capacity: number;
  notes: string | null;
  allocatedToday: number;
  rowVersion: string | null;
}

export interface FloorPlan {
  id: string;
  floorId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedAt: string;
}

export interface PlanPin {
  id: string;
  kind: PinKind;
  targetId: string;
  code: string;
  name: string;
  x: number;
  y: number;
}

export interface FloorView {
  floorId: string;
  floorCode: string;
  floorName: string;
  plan: FloorPlan | null;
  pins: PlanPin[];
  spaces: SpaceInfo[];
}

export interface Allocation {
  id: string;
  spaceId: string;
  spaceCode: string;
  spaceName: string;
  kind: OccupantKind;
  userId: string | null;
  userName: string | null;
  teamName: string | null;
  headcount: number;
  startOn: string;
  endOn: string | null;
  state: AllocationState;
  note: string | null;
  endReason: string | null;
  rowVersion: string;
}

export interface Occupancy {
  locationId: string;
  on: string;
  capacity: number;
  allocated: number;
  utilisationPercent: number | null;
  spacesWithoutCapacity: number;
  rows: { spaceId: string; code: string; name: string; kind: SpaceKind | null; capacity: number; allocated: number; utilisationPercent: number | null }[];
}

// ---------------- bookings ----------------

export type ResourceKind = 'Room' | 'Desk' | 'Parking' | 'Equipment' | 'Other';
export type BookingStatus = 'Pending' | 'Confirmed' | 'CheckedIn' | 'Rejected' | 'Cancelled' | 'NoShow';
export type DayName = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday';

export interface BookableResource {
  id: string;
  kind: ResourceKind;
  code: string;
  name: string;
  nameAr: string | null;
  spaceId: string | null;
  spaceName: string | null;
  capacity: number;
  timeZone: string;
  opensAt: string;
  closesAt: string;
  days: DayName[];
  minMinutes: number;
  maxMinutes: number;
  maxAdvanceDays: number;
  bufferMinutes: number;
  requiresApproval: boolean;
  checkInRequired: boolean;
  checkInGraceMinutes: number;
  notes: string | null;
  isArchived: boolean;
  rowVersion: string;
}

export interface Booking {
  id: string;
  resourceId: string;
  resourceCode: string;
  resourceName: string;
  resourceKind: ResourceKind;
  organizerId: string;
  organizerName: string | null;
  title: string;
  startsAt: string;
  endsAt: string;
  attendees: number;
  status: BookingStatus;
  seriesId: string | null;
  notes: string | null;
  decisionNote: string | null;
  cancelReason: string | null;
  checkedInAt: string | null;
  canCheckInNow: boolean;
  mine: boolean;
  rowVersion: string;
}

export interface Availability {
  resourceId: string;
  from: string;
  to: string;
  busy: { startsAt: string; endsAt: string; pending: boolean; mine: boolean }[];
}

export interface CalendarSync {
  enabled: boolean;
  adapter: string;
  availableAdapters: string[];
  rowVersion: string | null;
}

// ---------------- visitors ----------------

export type VisitKind = 'Guest' | 'Contractor' | 'Delivery' | 'Interview';
export type VisitStatus = 'Expected' | 'CheckedIn' | 'CheckedOut' | 'Cancelled' | 'NoShow';

export interface Visit {
  id: string;
  hostId: string;
  hostName: string;
  siteId: string;
  siteName: string;
  kind: VisitKind;
  visitorName: string;
  visitorCompany: string | null;
  visitorEmail: string | null;
  visitorPhone: string | null;
  purpose: string | null;
  expectedAt: string;
  expectedUntil: string | null;
  status: VisitStatus;
  checkedInAt: string | null;
  checkedOutAt: string | null;
  badgeNumber: string | null;
  inductionAt: string | null;
  cancelReason: string | null;
  canCheckInNow: boolean;
  anonymised: boolean;
  rowVersion: string;
}

export interface CreatedVisit {
  visit: Visit;
  passPath: string;
  passUrl: string;
  qrSvg: string;
}

export interface VisitPass {
  visitorName: string;
  siteName: string;
  hostName: string;
  expectedAt: string;
  expectedUntil: string | null;
  status: VisitStatus;
}

export interface WatchlistEntry {
  id: string;
  name: string;
  reason: string;
  addedAt: string;
}

export interface VisitorSettings {
  requireInduction: boolean;
  inductionText: string | null;
  retentionDays: number;
  rowVersion: string | null;
}

// ---------------- reports ----------------

export interface Kpi {
  key: string;
  group: string;
  value: number | null;
  unit: 'count' | 'percent' | 'hours' | 'minutes' | 'money' | 'area';
  detail: string | null;
}

export interface ReportSeries {
  key: string;
  group: string;
  unit: Kpi['unit'];
  points: { label: string; value: number }[];
}

export interface Dashboard {
  from: string;
  to: string;
  siteId: string | null;
  siteName: string | null;
  generatedAt: string;
  groups: { group: string; kpis: Kpi[]; series: ReportSeries[]; failed: boolean }[];
}

export interface FeedToken {
  id: string;
  name: string;
  prefix: string;
  scope: string;
  createdAt: string;
  expiresAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
  state: 'Active' | 'Expired' | 'Revoked';
}

export interface CreatedFeedToken {
  token: FeedToken;
  secret: string;
  feedPath: string;
}

export interface FeedDataset {
  name: string;
  columns: { name: string; type: string }[];
  timeColumn: string | null;
}

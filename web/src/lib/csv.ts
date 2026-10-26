export const LOCATION_TEMPLATE =
  'type,code,parentCode,name,nameAr,address,timeZone,areaSquareMetres\n' +
  'Site,RUH-HQ,,Riyadh HQ,المقر الرئيسي,King Fahd Rd,Asia/Riyadh,\n' +
  'Building,RUH-B1,RUH-HQ,Tower 1,البرج ١,,,12000\n' +
  'Floor,RUH-B1-F03,RUH-B1,Floor 3,الطابق ٣,,,900\n' +
  'Space,RUH-B1-F03-R301,RUH-B1-F03,Room 301,غرفة ٣٠١,,,25\n';

export const ASSET_TEMPLATE =
  'code,name,nameAr,classCode,locationCode,parentAssetCode,criticality,status,manufacturer,model,serialNumber,installDate,warrantyExpiry\n' +
  'CH-001,Chiller 1,مبرد ١,HVAC,RUH-B1-F03,,High,Active,Carrier,30XA,SN-1001,2024-01-15,2029-01-14\n' +
  'PU-001,Chilled water pump,مضخة المياه المبردة,HVAC,RUH-B1-F03,CH-001,Medium,Active,Grundfos,NK 80,SN-2001,2024-01-15,\n';

/** Starts a browser download of text (with a byte-order mark so Excel reads Arabic correctly). */
export function downloadText(fileName: string, text: string) {
  downloadBlob(fileName, new Blob(['﻿', text], { type: 'text/csv;charset=utf-8' }));
}

export function downloadBlob(fileName: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

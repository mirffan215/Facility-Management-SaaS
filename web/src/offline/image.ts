/**
 * Phone cameras produce 4-12 MB photos. Before a photo is queued it is scaled down (long side 1600 px) and re-encoded as JPEG, which keeps
 * it readable, fits several jobs' worth of photos on the phone, and sends quickly over a poor connection. If the browser cannot do it, or the
 * file is not a raster image, the original is kept.
 */
export async function preparePhoto(file: File, maxSide = 1600, quality = 0.8): Promise<{ blob: Blob; fileName: string; contentType: string }> {
  const original = { blob: file as Blob, fileName: file.name, contentType: file.type || 'application/octet-stream' };
  if (!/^image\/(jpeg|png|webp)$/.test(file.type) || typeof createImageBitmap !== 'function') return original;
  try {
    const bitmap = await createImageBitmap(file);
    const scale = Math.min(1, maxSide / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    const ctx = canvas.getContext('2d');
    if (!ctx) return original;
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close?.();
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', quality));
    if (!blob || (blob.size >= file.size && scale === 1)) return original;
    return { blob, fileName: file.name.replace(/\.[^.]+$/, '') + '.jpg', contentType: 'image/jpeg' };
  } catch {
    return original;
  }
}

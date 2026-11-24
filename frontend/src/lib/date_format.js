function pad_two(num) {
  return String(num).padStart(2, '0');
}

export function format_eu_datetime(value) {
  if (!value) return '--';
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return '--';
  const day = pad_two(date.getDate());
  const month = pad_two(date.getMonth() + 1);
  const year = date.getFullYear();
  const hours = pad_two(date.getHours());
  const minutes = pad_two(date.getMinutes());
  const seconds = pad_two(date.getSeconds());
  return `${day}/${month}/${year} ${hours}:${minutes}:${seconds}`;
}

export function format_eu_time(value, include_seconds = false) {
  if (!value) return '--';
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return '--';
  const hours = pad_two(date.getHours());
  const minutes = pad_two(date.getMinutes());
  if (include_seconds) {
    const seconds = pad_two(date.getSeconds());
    return `${hours}:${minutes}:${seconds}`;
  }
  return `${hours}:${minutes}`;
}

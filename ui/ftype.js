/// Значок формата для файла без превью.
///
/// Раньше здесь была своя контурная картинка на четыре случая: картинка,
/// видео, документ и лист бумаги на всё остальное. Лист доставался cdr, psd
/// и ai, то есть ровно тем форматам, которые у нас в архиве и лежат, и не
/// говорил о файле ничего.
///
/// Рисунки взяты из набора Suru++, см. ui/icons/NOTICE.md. Готовых наборов
/// со значком CorelDRAW почти не существует: у vscode-icons, Seti, Tabler и
/// Fluent его нет вовсе, у Material Icon Theme строка cdr по ошибке лежит
/// среди аудиоформатов, а у Numix и Zafiro файл с таким именем есть, но это
/// ссылка на обычный «векторный документ». Suru++ рисует настоящий шар.
///
/// Карта живёт отдельным файлом, потому что нужна и разбору, и библиотеке.
/// До этого она была скопирована в app.js и library.js слово в слово, и два
/// экрана могли начать спорить друг с другом.

const FTYPE = {
  cdr: 'cdr',
  psd: 'psd',
  ai: 'ai',
  eps: 'eps', ps: 'eps',
  pdf: 'pdf',

  jpg: 'jpg', jpeg: 'jpg',
  png: 'png',
  // Остальным растровым и векторным своего рисунка в наборе нет,
  // им достаётся общий значок картинки.
  gif: 'image', bmp: 'image', tif: 'image', tiff: 'image',
  webp: 'image', heic: 'image', svg: 'image', ico: 'image',

  mp4: 'video', mov: 'video', avi: 'video', mkv: 'video',
  webm: 'video', m4v: 'video', wmv: 'video',

  mp3: 'audio', wav: 'audio', flac: 'audio', ogg: 'audio',
  m4a: 'audio', aac: 'audio', wma: 'audio',

  doc: 'doc', docx: 'doc', odt: 'doc', rtf: 'doc',
  xls: 'xls', xlsx: 'xls', ods: 'xls', csv: 'xls',
  ppt: 'ppt', pptx: 'ppt', odp: 'ppt',

  txt: 'txt', md: 'txt', log: 'txt', json: 'txt', xml: 'txt',

  zip: 'zip', '7z': 'zip', tar: 'zip', gz: 'zip',
  rar: 'rar',
};

/// Имя файла значка. Расширение приходит из базы уже в нижнем регистре,
/// см. Ingest.cs, но проверка стоит дёшево, а перевод регистра в браузере
/// зависит от языка, поэтому просим ровно английский.
const ftype = ext => FTYPE[(ext || '').toLowerCase()] || 'file';

/// Разметка значка. Лежит под эскизом и прячется, когда тот загрузится.
const ftypeIcon = ext => `<img class="ficon" src="icons/${ftype(ext)}.svg" alt="">`;

/// Тот же значок, но мелкий и в углу. Появляется ровно в обратном случае:
/// когда эскиз загрузился и большой значок ушёл. Картинка показывает, что
/// внутри файла, но не говорит, чем его открывать, а макет в cdr, psd и ai
/// на превью выглядит одинаково.
const ftypeBadge = ext => `<img class="fbadge" src="icons/${ftype(ext)}.svg" alt="">`;

-- Tanba. Каталог файлов с тегами. SQLite.
-- Теги живут здесь, а не в файлах: работает для 100% форматов,
-- включая cdr, aep, prproj и папки. Файлы при разметке не трогаются.

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;


-- ФАЙЛЫ ------------------------------------------------------------------
-- Одна строка = один объект библиотеки: настоящий файл на диске либо каталог.

-- Каталоги лежат здесь же, строкой с kind='catalog'. Каталог показывается
-- в сетке как файл, ищется тегами как файл и попадает в отбор как файл,
-- поэтому отдельная таблица означала бы второй экземпляр тегов, поиска,
-- фасетов и карточки. Байтов у каталога нет: rel_path синтетический,
-- sha256 и ext пустые, size считается суммой содержимого.

CREATE TABLE files (
  id          INTEGER PRIMARY KEY,
  kind        TEXT    NOT NULL DEFAULT 'file',  -- file | catalog

  -- Путь = идентичность файла. Префикс имени содержит id с ведущими нулями,
  -- присваивается один раз и не меняется, даже если файл отредактировали.
  rel_path    TEXT    NOT NULL UNIQUE,  -- _store\2026\08\000147__1.mp4
  orig_name   TEXT    NOT NULL,         -- имя, с которым файл принесли: 1.mp4
  ext         TEXT,

  size        INTEGER,
  -- Хеш работает как атрибут. Идентичность задаёт путь. Файл изменили -> сканер обновляет
  -- эту колонку, id и теги остаются на месте. Используется для поиска дублей.
  sha256      TEXT,
  phash       TEXT,                     -- похожие картинки (перцептивный хеш)

  -- Номер файла на томе. Переживает переименование и перемещение, поэтому
  -- по нему файл узнаётся после того, как его переложили мимо программы.
  -- Хранится вместе с серийным номером тома, см. Shell/Native.FileId.
  file_id     TEXT,

  width       INTEGER,
  height      INTEGER,
  duration_ms INTEGER,
  thumb       TEXT,                     -- путь к превью

  -- Три разные даты, их нельзя путать. Отбор в библиотеке идёт по первым двум,
  -- переключателем: создан / изменён / любая из них.
  file_ctime  INTEGER,                  -- когда файл создан (NTFS хранит и переживает перенос)
  file_mtime  INTEGER,                  -- когда файл изменён
  added_at    INTEGER NOT NULL,         -- когда попал в библиотеку
  seen_at     INTEGER,                  -- когда сканер последний раз его видел
  is_missing  INTEGER NOT NULL DEFAULT 0,

  -- Файл уехал за пределы хранилища, но остался на диске: сюда пишем, где он
  -- теперь лежит. Внутри хранилища переезд отслеживается молча, сменой
  -- rel_path, и сюда не попадает.
  moved_to    TEXT,
  note        TEXT,

  -- Обложка каталога. Берётся с лучшего рендера внутри, а не с исходника:
  -- у cdr эскиз бывает пустым, у картинки рядом он всегда хороший.
  cover_file_id INTEGER REFERENCES files(id) ON DELETE SET NULL
);

CREATE INDEX ix_files_sha     ON files(sha256);
CREATE INDEX ix_files_fileid  ON files(file_id);
CREATE INDEX ix_files_phash   ON files(phash);
CREATE INDEX ix_files_ext     ON files(ext);
CREATE INDEX ix_files_added   ON files(added_at);
CREATE INDEX ix_files_ctime   ON files(file_ctime);
CREATE INDEX ix_files_mtime   ON files(file_mtime);
CREATE INDEX ix_files_missing ON files(is_missing) WHERE is_missing = 1;
CREATE INDEX ix_files_kind    ON files(kind);


-- СОСТАВ КАТАЛОГА --------------------------------------------------------
-- Файл может лежать в скольких угодно каталогах: каталог это не папка,
-- а связь, которую видно. Каталог внутри каталога допускается,
-- но ровно в одном родителе, иначе пропадает понятие «самый верхний»
-- и появляются циклы.

CREATE TABLE catalog_items (
  catalog_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  item_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  sort_order INTEGER NOT NULL DEFAULT 0,
  added_at   INTEGER NOT NULL,
  PRIMARY KEY (catalog_id, item_id)
) WITHOUT ROWID;

CREATE INDEX ix_ci_item ON catalog_items(item_id);

-- Единственный родитель у вложенного каталога стережёт база, а не код:
-- забыть проверку в одном из путей записи слишком легко.
CREATE TRIGGER catalog_single_parent
BEFORE INSERT ON catalog_items
WHEN (SELECT kind FROM files WHERE id = NEW.item_id) = 'catalog'
     AND EXISTS (SELECT 1 FROM catalog_items WHERE item_id = NEW.item_id)
BEGIN
  SELECT RAISE(ABORT, 'каталог уже лежит в другом каталоге');
END;

-- Сам в себя каталог не кладётся. Более длинные циклы ловит код:
-- рекурсию в WHEN триггера SQLite не пускает.
CREATE TRIGGER catalog_not_self
BEFORE INSERT ON catalog_items
WHEN NEW.catalog_id = NEW.item_id
BEGIN
  SELECT RAISE(ABORT, 'каталог не может лежать сам в себе');
END;


-- ГРУППЫ ТЕГОВ -----------------------------------------------------------
-- Организация, Тип, Кампания, Человек, Статус: всё это группы тегов.
-- Все группы устроены ОДИНАКОВО. Разница только в названии, содержимом
-- и в том, можно ли выбрать несколько тегов сразу.

CREATE TABLE tag_groups (
  id          INTEGER PRIMARY KEY,
  name        TEXT    NOT NULL UNIQUE,
  is_multi    INTEGER NOT NULL DEFAULT 1,  -- 0 = на файле только один тег из группы
  is_required INTEGER NOT NULL DEFAULT 0,  -- без тега из этой группы файл неразмечен
  sort_order  INTEGER NOT NULL DEFAULT 0
);


-- ТЕГИ -------------------------------------------------------------------
-- parent_id даёт вложенность ВНУТРИ группы: Профориентация -> Профориентация 2026.
-- Переименование тега = один UPDATE, файлы не трогаются вообще.

CREATE TABLE tags (
  id         INTEGER PRIMARY KEY,
  group_id   INTEGER NOT NULL REFERENCES tag_groups(id) ON DELETE CASCADE,
  parent_id  INTEGER          REFERENCES tags(id)       ON DELETE CASCADE,
  name       TEXT    NOT NULL,
  sort_order INTEGER NOT NULL DEFAULT 0
);

-- ifnull(parent_id,0) обязателен: в SQLite два NULL считаются разными,
-- и без этого два корневых тега с одинаковым именем прошли бы в одну группу.
CREATE UNIQUE INDEX ux_tags_name ON tags(group_id, ifnull(parent_id, 0), name);
CREATE INDEX ix_tags_group  ON tags(group_id);
CREATE INDEX ix_tags_parent ON tags(parent_id);


-- СВЯЗЬ ФАЙЛ <-> ТЕГ -----------------------------------------------------
-- Ядро всей затеи. Один файл получает сколько угодно тегов, и наоборот.

CREATE TABLE file_tags (
  file_id  INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  tag_id   INTEGER NOT NULL REFERENCES tags(id)  ON DELETE CASCADE,
  source   TEXT    NOT NULL DEFAULT 'manual',    -- manual | auto | import
  added_at INTEGER NOT NULL,
  PRIMARY KEY (file_id, tag_id)
) WITHOUT ROWID;

CREATE INDEX ix_ft_tag ON file_tags(tag_id);


-- ПОДБОРКИ ---------------------------------------------------------------
-- Отличаются от тегов тем, что состав ручной и порядок важен.

CREATE TABLE collections (
  id         INTEGER PRIMARY KEY,
  name       TEXT    NOT NULL UNIQUE,
  note       TEXT,
  created_at INTEGER NOT NULL
);

CREATE TABLE collection_items (
  collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
  file_id       INTEGER NOT NULL REFERENCES files(id)       ON DELETE CASCADE,
  sort_order    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (collection_id, file_id)
) WITHOUT ROWID;


-- ПОИСК ------------------------------------------------------------------
-- unicode61 корректно режет кириллицу и казахские буквы (ә, ө, ұ, қ, ғ, ң, і),
-- и он же режет имя по точкам и подчёркиваниям: 000147__промо.mp4 попадает
-- в указатель как «000147», «промо», «mp4». Поэтому порядок слов не важен
-- и «унив реклама» находит «Реклама универа».
--
-- Только имя. Теги отбираются флажками через file_tags, печатать их незачем,
-- а текста из документов у нас пока нет: появится распознавание pdf, тогда
-- и добавим колонку, вместе с кодом, который её заполняет. Объявлять впрок
-- уже пробовали, эта таблица три месяца простояла пустой.
--
-- rowid = files.id, строки поддерживает приложение (Repo.Index).

CREATE VIRTUAL TABLE files_fts USING fts5(
  name,
  tokenize = "unicode61 remove_diacritics 2"
);


-- ЖУРНАЛ И НАСТРОЙКИ -----------------------------------------------------

CREATE TABLE events (
  id      INTEGER PRIMARY KEY,
  at      INTEGER NOT NULL,
  kind    TEXT    NOT NULL,   -- tag_add | tag_remove | file_add | rename | ...
  file_id INTEGER,
  tag_id  INTEGER,
  payload TEXT
);
CREATE INDEX ix_events_at ON events(at);

CREATE TABLE settings (k TEXT PRIMARY KEY, v TEXT);


-- МУСОР ------------------------------------------------------------------
-- Что сканер не берёт в каталог. action='skip' значит пройти мимо,
-- action='version' значит привязать как версию к родительскому файлу.

CREATE TABLE ignore_rules (
  id      INTEGER PRIMARY KEY,
  pattern TEXT    NOT NULL UNIQUE,
  action  TEXT    NOT NULL DEFAULT 'skip',  -- skip | version
  note    TEXT
);

INSERT INTO ignore_rules (pattern, action, note) VALUES
  ('Резервная копия_*.cdr', 'version', 'CorelDRAW, резервная копия при сохранении'),
  ('Backup_of_*.cdr',       'version', 'CorelDRAW, англ. локализация'),
  ('Автосохранение_*.cdr',  'skip',    'CorelDRAW, автосохранение'),
  ('AutoBackup_of_*.cdr',   'skip',    'CorelDRAW, автосохранение, англ.'),
  ('*.bak',                 'version', 'общий шаблон резервных копий'),
  ('*.tmp',                 'skip',    NULL),
  ('~$*',                   'skip',    'временные файлы MS Office'),
  ('Thumbs.db',             'skip',    NULL),
  ('desktop.ini',           'skip',    NULL),
  ('.DS_Store',             'skip',    'мусор macOS с чужих флешек'),
  ('Adobe After Effects Auto-Save\', 'skip', 'папка автосохранения AE'),
  ('Adobe Premiere Pro Auto-Save\',  'skip', 'папка автосохранения Premiere'),
  ('*.idlk',                'skip',    'файлы блокировки InDesign');


-- СОХРАНЁННЫЕ ОТБОРЫ -----------------------------------------------------
-- Кнопка «Сохранить отбор» в строке отбора. Не путать с подборками:
-- здесь хранится ЗАПРОС, а состав пересчитывается каждый раз.

CREATE TABLE saved_queries (
  id         INTEGER PRIMARY KEY,
  name       TEXT    NOT NULL UNIQUE,
  mode       TEXT    NOT NULL DEFAULT 'and',  -- and | or
  tag_ids    TEXT    NOT NULL,                -- json-массив id тегов
  ext_list   TEXT,                            -- json-массив расширений: формат отбирается наравне с тегами
  sort_order INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL
);


-- ВЕРСИИ -----------------------------------------------------------------
-- Лента версий на карточке файла. Сюда же уезжают резервные копии Corel,
-- пойманные правилом ignore_rules с action='version'.

CREATE TABLE versions (
  id         INTEGER PRIMARY KEY,
  file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  rel_path   TEXT    NOT NULL UNIQUE,  -- _versions\000147\2026-08-06T14-22.cdr
  size       INTEGER,
  sha256     TEXT,
  file_mtime INTEGER,
  saved_at   INTEGER NOT NULL,
  origin     TEXT    NOT NULL DEFAULT 'edit'  -- edit | corel_backup | manual
);

CREATE INDEX ix_versions_file ON versions(file_id);


-- ПРЕДСТАВЛЕНИЯ ----------------------------------------------------------

-- Разбор завалов: что ещё не размечено
CREATE VIEW v_untagged AS
SELECT f.*
FROM files f
WHERE f.is_missing = 0
  AND NOT EXISTS (SELECT 1 FROM file_tags ft WHERE ft.file_id = f.id);

-- Дубликаты и сколько места они съели
CREATE VIEW v_dupes AS
SELECT sha256,
       COUNT(*)              AS copies,
       SUM(size) - MIN(size) AS wasted
FROM files
WHERE sha256 IS NOT NULL AND kind = 'file'
GROUP BY sha256
HAVING COUNT(*) > 1;


-- ЗАГОТОВКА ГРУПП И ТЕГОВ ------------------------------------------------

-- Года среди групп нет намеренно: дата непрерывна и уже лежит в files.
-- В библиотеке она отбирается диапазоном «с … до», а не тегом.
INSERT INTO tag_groups (name, is_multi, is_required, sort_order) VALUES
  ('Организация', 1, 1, 1),
  ('Тип',         1, 1, 2),
  ('Кампания',    1, 0, 3),
  ('Человек',     1, 0, 4),
  ('Статус',      0, 0, 5);

INSERT INTO tags (group_id, name) SELECT id, 'Digital College' FROM tag_groups WHERE name='Организация';
INSERT INTO tags (group_id, name) SELECT id, 'Elevate'         FROM tag_groups WHERE name='Организация';
INSERT INTO tags (group_id, name) SELECT id, 'IT School'       FROM tag_groups WHERE name='Организация';
INSERT INTO tags (group_id, name) SELECT id, 'QAZIITU'         FROM tag_groups WHERE name='Организация';
INSERT INTO tags (group_id, name) SELECT id, 'RVTK'            FROM tag_groups WHERE name='Организация';

INSERT INTO tags (group_id, name) SELECT id, 'Баннер'      FROM tag_groups WHERE name='Тип';
INSERT INTO tags (group_id, name) SELECT id, 'Видео'       FROM tag_groups WHERE name='Тип';
INSERT INTO tags (group_id, name) SELECT id, 'Заставка'    FROM tag_groups WHERE name='Тип';
INSERT INTO tags (group_id, name) SELECT id, 'Презентация' FROM tag_groups WHERE name='Тип';

INSERT INTO tags (group_id, name) SELECT id, 'Профориентация' FROM tag_groups WHERE name='Кампания';
INSERT INTO tags (group_id, name) SELECT id, 'Магистратура'   FROM tag_groups WHERE name='Кампания';

INSERT INTO tags (group_id, name) SELECT id, 'Аида М' FROM tag_groups WHERE name='Человек';

INSERT INTO tags (group_id, name) SELECT id, 'Черновик'     FROM tag_groups WHERE name='Статус';
INSERT INTO tags (group_id, name) SELECT id, 'Утверждено'   FROM tag_groups WHERE name='Статус';
INSERT INTO tags (group_id, name) SELECT id, 'Опубликовано' FROM tag_groups WHERE name='Статус';

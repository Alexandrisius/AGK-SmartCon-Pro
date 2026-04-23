using System.Windows;
using SmartCon.Core.Services;

namespace SmartCon.UI;

public static class StringLocalization
{
    public static ResourceDictionary BuildResourceDictionary(Language lang)
    {
        return lang == Language.EN ? BuildEn() : BuildRu();
    }

    private static ResourceDictionary BuildRu()
    {
        var d = new ResourceDictionary();

        d[Keys.Btn_Cancel] = "Отмена";
        d[Keys.Btn_Connect] = "Соединить";
        d[Keys.Btn_Insert] = "Вставить";
        d[Keys.Btn_Change] = "Изменить";
        d[Keys.Btn_Save] = "Сохранить";
        d[Keys.Btn_Saved] = "✓ Сохранено";
        d[Keys.Btn_Delete] = "Удалить";
        d[Keys.Btn_Add] = "+ Добавить";
        d[Keys.Btn_Import] = "Импорт…";
        d[Keys.Btn_Export] = "Экспорт…";
        d[Keys.Tip_Import] = "Загрузить настройки маппинга из JSON-файла (перезапишет текущие)";
        d[Keys.Tip_Export] = "Сохранить настройки маппинга в JSON-файл";
        d[Keys.Btn_Confirm] = "Подтвердить";
        d[Keys.Btn_Close] = "Закрыть";
        d[Keys.Btn_OK] = "OK";

        d[Keys.Title_EditorSetup] = "Настройка соединения элементов";
        d[Keys.Heading_EditorSetup] = "Настройка соединения элементов";
        d[Keys.Title_Settings] = "Настройки SmartCon";
        d[Keys.Title_FamilySelector] = "Выбор семейств фитингов";
        d[Keys.Title_ConnType] = "Тип соединения";
        d[Keys.Title_CtcSetup] = "Назначение типов коннекторов";
        d[Keys.Title_About] = "О SmartCon";

        d[Keys.Tip_RotateCCW] = "Повернуть против часовой стрелки";
        d[Keys.Tip_RotationAngle] = "Угол поворота в градусах";
        d[Keys.Tip_RotateCW] = "Повернуть по часовой стрелке";
        d[Keys.Tip_AvailableSizes] = "Доступные типоразмеры динамического семейства";
        d[Keys.Tip_ChangeSize] = "Изменить размер динамического семейства";
        d[Keys.Tip_FittingCtc] = "Отразить типы коннекторов фитинга";
        d[Keys.Tip_SelectFitting] = "Выберите фитинг для соединения";
        d[Keys.Tip_InsertFitting] = "Вставить выбранный фитинг (примерка)";
        d[Keys.Tip_ReducerCtc] = "Отразить типы коннекторов переходника";
        d[Keys.Tip_SelectReducer] = "Выберите переходник";
        d[Keys.Tip_InsertReducer] = "Вставить выбранный переходник";
        d[Keys.Tip_ChainDecrement] = "Отсоединить последний уровень";
        d[Keys.Tip_ChainIncrement] = "Присоединить следующий уровень";
        d[Keys.Tip_ConnectAll] = "Подключить все элементы сети сразу";
        d[Keys.Tip_SelectFittingFamilies] = "Нажмите для выбора семейств фитингов (недоступно при «Прямое» — используйте переходы сечения)";
        d[Keys.Tip_SelectReducerFamilies] = "Нажмите для выбора семейств переходников";
        d[Keys.Tip_ChangeConnection] = "Переключить на другой свободный коннектор";
        d[Keys.Tip_Connect] = "Выполнить соединение элементов";

        d[Keys.Label_Size] = "Размер:";
        d[Keys.Label_Network] = "Сеть:";
        d[Keys.Label_Updates] = "Обновления";
        d[Keys.Label_AvailableFamilies] = "Доступные семейства:";
        d[Keys.Label_FamilyFilter] = "(OST_PipeFitting, MultiPort, 2 коннектора)";
        d[Keys.Label_SelectedFamilies] = "Выбранные (порядок = приоритет):";
        d[Keys.Label_Family] = "Семейство: ";
        d[Keys.Label_Symbol] = "Типоразмер: ";
        d[Keys.Label_Connector] = "Коннектор ";

        d[Keys.Btn_ChangeConnection] = "Изменить соединение";
        d[Keys.Btn_ConnectAll] = "Подключить всё";
        d[Keys.Btn_AddFamily] = "▶▶ Добавить";
        d[Keys.Btn_RemoveFamily] = "◀◀ Убрать";
        d[Keys.Btn_Up] = "▲ Вверх";
        d[Keys.Btn_Down] = "▼ Вниз";
        d[Keys.Btn_CheckUpdates] = "Проверить обновления";
        d[Keys.Btn_DownloadInstall] = "Скачать и установить";
        d[Keys.Chk_CheckOnStartup] = "Проверять при запуске";

        d[Keys.Tab_ConnectorTypes] = "Типы коннекторов";
        d[Keys.Tab_MappingRules] = "Правила маппинга";

        d[Keys.Col_Code] = "Код";
        d[Keys.Col_Name] = "Название";
        d[Keys.Col_Description] = "Описание";
        d[Keys.Col_ConnType1] = "Тип соединения 1";
        d[Keys.Col_ConnType2] = "Тип соединения 2";
        d[Keys.Col_Direct] = "Прямое";
        d[Keys.Col_Fittings] = "Фитинги";
        d[Keys.Col_Transitions] = "Переходы сечения";

        d[Keys.Status_Active] = "Активно";
        d[Keys.Status_Processing] = "Обработка…";
        d[Keys.Status_SessionEnded] = "Сессия завершена";
        d[Keys.Msg_AssignCtc] = "У фитинга не заданы типы коннекторов. Назначьте тип каждому коннектору:";

        d[Keys.About_Author] = "Автор:";
        d[Keys.About_Plugin] = "Плагин:";
        d[Keys.About_PluginDesc] = "MEP коннектор труб для Revit 2025";
        d[Keys.About_Repo] = "Репозиторий:";
        d[Keys.About_Language] = "Язык:";

        d[Keys.Mapping_NewType] = "Новый тип";
        d[Keys.Mapping_NotSelected] = "(не выбраны)";
        d[Keys.Fitting_DirectConnect] = "Без фитинга (прямое соединение)";
        d[Keys.Fitting_TypeArrow] = "Тип {0} → {1}";

        d[Keys.PM_Title_Settings] = "Настройки шаринга";
        d[Keys.PM_Title_Progress] = "Шаринг проекта";
        d[Keys.PM_Tab_General] = "Общие";
        d[Keys.PM_Tab_Purge] = "Очистка";
        d[Keys.PM_Tab_Views] = "Виды";
        d[Keys.PM_Tab_Naming] = "Именование";
        d[Keys.PM_CurrentFile] = "Текущий файл";
        d[Keys.PM_FilePath] = "Путь:";
        d[Keys.PM_Folder] = "Папка:";
        d[Keys.PM_FileName] = "Имя:";
        d[Keys.PM_SharedFolder] = "Папка Shared";
        d[Keys.PM_Browse] = "Обзор...";
        d[Keys.PM_SyncBefore] = "Синхронизировать перед шарингом";
        d[Keys.PM_PurgeTitle] = "Выберите элементы для удаления из Shared-файла:";
        d[Keys.PM_PurgeRvtLinks] = "RVT-связи";
        d[Keys.PM_PurgeCadImports] = "CAD / IFC-связи";
        d[Keys.PM_PurgeImages] = "Растровые изображения";
        d[Keys.PM_PurgePointClouds] = "Облака точек";
        d[Keys.PM_PurgeGroups] = "Группы и типы групп";
        d[Keys.PM_PurgeAssemblies] = "Сборки";
        d[Keys.PM_PurgeSpaces] = "MEP-пространства";
        d[Keys.PM_PurgeRebar] = "Арматура (Rebar)";
        d[Keys.PM_PurgeFabric] = "Арматурные каркасы";
        d[Keys.PM_PurgeSheets] = "Листы и чертежи";
        d[Keys.PM_PurgeSchedules] = "Спецификации";
        d[Keys.PM_PurgeUnused] = "Неиспользуемые элементы (Purge)";
        d[Keys.PM_SelectAll] = "Все";
        d[Keys.PM_DeselectAll] = "Ни одного";
        d[Keys.PM_ViewsTitle] = "Виды для сохранения в Shared-файле:";
        d[Keys.PM_RefreshViews] = "Обновить список";
        d[Keys.PM_SearchViews] = "Поиск вида...";
        d[Keys.PM_SelectedCount] = "Выбрано:";
        d[Keys.PM_NamingTemplate] = "Шаблон имени файла";
        d[Keys.PM_Delimiter] = "Разделитель:";
        d[Keys.PM_Blocks] = "Блоки имени:";
        d[Keys.PM_StatusMappings] = "Маппинг статусов:";
        d[Keys.PM_Preview] = "Превью";
        d[Keys.PM_Col_Index] = "№";
        d[Keys.PM_Col_Role] = "Роль";
        d[Keys.PM_Col_Label] = "Метка";
        d[Keys.PM_Col_Wip] = "WIP";
        d[Keys.PM_Col_Shared] = "Shared";
        d[Keys.PM_StatusMarker] = "*";
        d[Keys.PM_CurrentName] = "Текущее:";
        d[Keys.PM_SharedName] = "Shared:";
        d[Keys.PM_AddBlock] = "+ Добавить";
        d[Keys.PM_RemoveBlock] = "- Удалить";
        d[Keys.PM_AddMapping] = "+ Добавить";
        d[Keys.PM_RemoveMapping] = "- Удалить";
        d[Keys.PM_ParseFromName] = "Разобрать имя файла";

        return d;
    }

    private static ResourceDictionary BuildEn()
    {
        var d = new ResourceDictionary();

        d[Keys.Btn_Cancel] = "Cancel";
        d[Keys.Btn_Connect] = "Connect";
        d[Keys.Btn_Insert] = "Insert";
        d[Keys.Btn_Change] = "Change";
        d[Keys.Btn_Save] = "Save";
        d[Keys.Btn_Saved] = "✓ Saved";
        d[Keys.Btn_Delete] = "Delete";
        d[Keys.Btn_Add] = "+ Add";
        d[Keys.Btn_Import] = "Import…";
        d[Keys.Btn_Export] = "Export…";
        d[Keys.Tip_Import] = "Load mapping settings from a JSON file (overwrites current)";
        d[Keys.Tip_Export] = "Save mapping settings to a JSON file";
        d[Keys.Btn_Confirm] = "Confirm";
        d[Keys.Btn_Close] = "Close";
        d[Keys.Btn_OK] = "OK";

        d[Keys.Title_EditorSetup] = "Connection Setup";
        d[Keys.Heading_EditorSetup] = "Connection Setup";
        d[Keys.Title_Settings] = "SmartCon Settings";
        d[Keys.Title_FamilySelector] = "Select Fitting Families";
        d[Keys.Title_ConnType] = "Connection Type";
        d[Keys.Title_CtcSetup] = "Assign Connector Types";
        d[Keys.Title_About] = "About SmartCon";

        d[Keys.Tip_RotateCCW] = "Rotate counter-clockwise";
        d[Keys.Tip_RotationAngle] = "Rotation angle in degrees";
        d[Keys.Tip_RotateCW] = "Rotate clockwise";
        d[Keys.Tip_AvailableSizes] = "Available dynamic family sizes";
        d[Keys.Tip_ChangeSize] = "Change dynamic family size";
        d[Keys.Tip_FittingCtc] = "Reflect fitting connector types";
        d[Keys.Tip_SelectFitting] = "Select fitting for connection";
        d[Keys.Tip_InsertFitting] = "Insert selected fitting (preview)";
        d[Keys.Tip_ReducerCtc] = "Reflect reducer connector types";
        d[Keys.Tip_SelectReducer] = "Select reducer";
        d[Keys.Tip_InsertReducer] = "Insert selected reducer";
        d[Keys.Tip_ChainDecrement] = "Disconnect last level";
        d[Keys.Tip_ChainIncrement] = "Connect next level";
        d[Keys.Tip_ConnectAll] = "Connect all network elements at once";
        d[Keys.Tip_SelectFittingFamilies] = "Click to select fitting families (unavailable for Direct — use cross-section transitions)";
        d[Keys.Tip_SelectReducerFamilies] = "Click to select reducer families";
        d[Keys.Tip_ChangeConnection] = "Switch to another free connector";
        d[Keys.Tip_Connect] = "Execute element connection";

        d[Keys.Label_Size] = "Size:";
        d[Keys.Label_Network] = "Network:";
        d[Keys.Label_Updates] = "Updates";
        d[Keys.Label_AvailableFamilies] = "Available families:";
        d[Keys.Label_FamilyFilter] = "(OST_PipeFitting, MultiPort, 2 connectors)";
        d[Keys.Label_SelectedFamilies] = "Selected (order = priority):";
        d[Keys.Label_Family] = "Family: ";
        d[Keys.Label_Symbol] = "Type: ";
        d[Keys.Label_Connector] = "Connector ";

        d[Keys.Btn_ChangeConnection] = "Change connection";
        d[Keys.Btn_ConnectAll] = "Connect all";
        d[Keys.Btn_AddFamily] = "▶▶ Add";
        d[Keys.Btn_RemoveFamily] = "◀◀ Remove";
        d[Keys.Btn_Up] = "▲ Up";
        d[Keys.Btn_Down] = "▼ Down";
        d[Keys.Btn_CheckUpdates] = "Check for updates";
        d[Keys.Btn_DownloadInstall] = "Download and install";
        d[Keys.Chk_CheckOnStartup] = "Check on startup";

        d[Keys.Tab_ConnectorTypes] = "Connector Types";
        d[Keys.Tab_MappingRules] = "Mapping Rules";

        d[Keys.Col_Code] = "Code";
        d[Keys.Col_Name] = "Name";
        d[Keys.Col_Description] = "Description";
        d[Keys.Col_ConnType1] = "Connection Type 1";
        d[Keys.Col_ConnType2] = "Connection Type 2";
        d[Keys.Col_Direct] = "Direct";
        d[Keys.Col_Fittings] = "Fittings";
        d[Keys.Col_Transitions] = "Cross-Section Transitions";

        d[Keys.Status_Active] = "Active";
        d[Keys.Status_Processing] = "Processing…";
        d[Keys.Status_SessionEnded] = "Session ended";
        d[Keys.Msg_AssignCtc] = "Fitting has no connector types assigned. Assign a type to each connector:";

        d[Keys.About_Author] = "Author:";
        d[Keys.About_Plugin] = "Plugin:";
        d[Keys.About_PluginDesc] = "MEP Pipe Connector for Revit 2025";
        d[Keys.About_Repo] = "Repo:";
        d[Keys.About_Language] = "Language:";

        d[Keys.Mapping_NewType] = "New type";
        d[Keys.Mapping_NotSelected] = "(not selected)";
        d[Keys.Fitting_DirectConnect] = "No fitting (direct connect)";
        d[Keys.Fitting_TypeArrow] = "Type {0} → {1}";

        d[Keys.PM_Title_Settings] = "Share Settings";
        d[Keys.PM_Title_Progress] = "Sharing Project";
        d[Keys.PM_Tab_General] = "General";
        d[Keys.PM_Tab_Purge] = "Purge";
        d[Keys.PM_Tab_Views] = "Views";
        d[Keys.PM_Tab_Naming] = "Naming";
        d[Keys.PM_CurrentFile] = "Current File";
        d[Keys.PM_FilePath] = "Path:";
        d[Keys.PM_Folder] = "Folder:";
        d[Keys.PM_FileName] = "Name:";
        d[Keys.PM_SharedFolder] = "Shared Folder";
        d[Keys.PM_Browse] = "Browse...";
        d[Keys.PM_SyncBefore] = "Synchronize before sharing";
        d[Keys.PM_PurgeTitle] = "Select elements to remove from the Shared file:";
        d[Keys.PM_PurgeRvtLinks] = "RVT Links";
        d[Keys.PM_PurgeCadImports] = "CAD / IFC Links";
        d[Keys.PM_PurgeImages] = "Raster Images";
        d[Keys.PM_PurgePointClouds] = "Point Clouds";
        d[Keys.PM_PurgeGroups] = "Groups and Group Types";
        d[Keys.PM_PurgeAssemblies] = "Assemblies";
        d[Keys.PM_PurgeSpaces] = "MEP Spaces";
        d[Keys.PM_PurgeRebar] = "Rebar";
        d[Keys.PM_PurgeFabric] = "Fabric Reinforcement";
        d[Keys.PM_PurgeSheets] = "Sheets and Drawings";
        d[Keys.PM_PurgeSchedules] = "Schedules";
        d[Keys.PM_PurgeUnused] = "Unused Elements (Purge)";
        d[Keys.PM_SelectAll] = "All";
        d[Keys.PM_DeselectAll] = "None";
        d[Keys.PM_ViewsTitle] = "Views to keep in the Shared file:";
        d[Keys.PM_RefreshViews] = "Refresh List";
        d[Keys.PM_SearchViews] = "Search views...";
        d[Keys.PM_SelectedCount] = "Selected:";
        d[Keys.PM_NamingTemplate] = "File Name Template";
        d[Keys.PM_Delimiter] = "Delimiter:";
        d[Keys.PM_Blocks] = "Name Blocks:";
        d[Keys.PM_StatusMappings] = "Status Mappings:";
        d[Keys.PM_Preview] = "Preview";
        d[Keys.PM_Col_Index] = "#";
        d[Keys.PM_Col_Role] = "Role";
        d[Keys.PM_Col_Label] = "Label";
        d[Keys.PM_Col_Wip] = "WIP";
        d[Keys.PM_Col_Shared] = "Shared";
        d[Keys.PM_StatusMarker] = "*";
        d[Keys.PM_CurrentName] = "Current:";
        d[Keys.PM_SharedName] = "Shared:";
        d[Keys.PM_AddBlock] = "+ Add";
        d[Keys.PM_RemoveBlock] = "- Remove";
        d[Keys.PM_AddMapping] = "+ Add";
        d[Keys.PM_RemoveMapping] = "- Remove";
        d[Keys.PM_ParseFromName] = "Parse File Name";

        return d;
    }

    public static class Keys
    {
        public const string Btn_Cancel = nameof(Btn_Cancel);
        public const string Btn_Connect = nameof(Btn_Connect);
        public const string Btn_Insert = nameof(Btn_Insert);
        public const string Btn_Change = nameof(Btn_Change);
        public const string Btn_Save = nameof(Btn_Save);
        public const string Btn_Saved = nameof(Btn_Saved);
        public const string Btn_Delete = nameof(Btn_Delete);
        public const string Btn_Add = nameof(Btn_Add);
        public const string Btn_Import = nameof(Btn_Import);
        public const string Btn_Export = nameof(Btn_Export);
        public const string Tip_Import = nameof(Tip_Import);
        public const string Tip_Export = nameof(Tip_Export);
        public const string Btn_Confirm = nameof(Btn_Confirm);
        public const string Btn_Close = nameof(Btn_Close);
        public const string Btn_OK = nameof(Btn_OK);

        public const string Title_EditorSetup = nameof(Title_EditorSetup);
        public const string Heading_EditorSetup = nameof(Heading_EditorSetup);
        public const string Title_Settings = nameof(Title_Settings);
        public const string Title_FamilySelector = nameof(Title_FamilySelector);
        public const string Title_ConnType = nameof(Title_ConnType);
        public const string Title_CtcSetup = nameof(Title_CtcSetup);
        public const string Title_About = nameof(Title_About);

        public const string Tip_RotateCCW = nameof(Tip_RotateCCW);
        public const string Tip_RotationAngle = nameof(Tip_RotationAngle);
        public const string Tip_RotateCW = nameof(Tip_RotateCW);
        public const string Tip_AvailableSizes = nameof(Tip_AvailableSizes);
        public const string Tip_ChangeSize = nameof(Tip_ChangeSize);
        public const string Tip_FittingCtc = nameof(Tip_FittingCtc);
        public const string Tip_SelectFitting = nameof(Tip_SelectFitting);
        public const string Tip_InsertFitting = nameof(Tip_InsertFitting);
        public const string Tip_ReducerCtc = nameof(Tip_ReducerCtc);
        public const string Tip_SelectReducer = nameof(Tip_SelectReducer);
        public const string Tip_InsertReducer = nameof(Tip_InsertReducer);
        public const string Tip_ChainDecrement = nameof(Tip_ChainDecrement);
        public const string Tip_ChainIncrement = nameof(Tip_ChainIncrement);
        public const string Tip_ConnectAll = nameof(Tip_ConnectAll);
        public const string Tip_SelectFittingFamilies = nameof(Tip_SelectFittingFamilies);
        public const string Tip_SelectReducerFamilies = nameof(Tip_SelectReducerFamilies);
        public const string Tip_ChangeConnection = nameof(Tip_ChangeConnection);
        public const string Tip_Connect = nameof(Tip_Connect);

        public const string Label_Size = nameof(Label_Size);
        public const string Label_Network = nameof(Label_Network);
        public const string Label_Updates = nameof(Label_Updates);
        public const string Label_AvailableFamilies = nameof(Label_AvailableFamilies);
        public const string Label_FamilyFilter = nameof(Label_FamilyFilter);
        public const string Label_SelectedFamilies = nameof(Label_SelectedFamilies);
        public const string Label_Family = nameof(Label_Family);
        public const string Label_Symbol = nameof(Label_Symbol);
        public const string Label_Connector = nameof(Label_Connector);

        public const string Btn_ChangeConnection = nameof(Btn_ChangeConnection);
        public const string Btn_ConnectAll = nameof(Btn_ConnectAll);
        public const string Btn_AddFamily = nameof(Btn_AddFamily);
        public const string Btn_RemoveFamily = nameof(Btn_RemoveFamily);
        public const string Btn_Up = nameof(Btn_Up);
        public const string Btn_Down = nameof(Btn_Down);
        public const string Btn_CheckUpdates = nameof(Btn_CheckUpdates);
        public const string Btn_DownloadInstall = nameof(Btn_DownloadInstall);
        public const string Chk_CheckOnStartup = nameof(Chk_CheckOnStartup);

        public const string Tab_ConnectorTypes = nameof(Tab_ConnectorTypes);
        public const string Tab_MappingRules = nameof(Tab_MappingRules);

        public const string Col_Code = nameof(Col_Code);
        public const string Col_Name = nameof(Col_Name);
        public const string Col_Description = nameof(Col_Description);
        public const string Col_ConnType1 = nameof(Col_ConnType1);
        public const string Col_ConnType2 = nameof(Col_ConnType2);
        public const string Col_Direct = nameof(Col_Direct);
        public const string Col_Fittings = nameof(Col_Fittings);
        public const string Col_Transitions = nameof(Col_Transitions);

        public const string Status_Active = nameof(Status_Active);
        public const string Status_Processing = nameof(Status_Processing);
        public const string Status_SessionEnded = nameof(Status_SessionEnded);
        public const string Msg_AssignCtc = nameof(Msg_AssignCtc);

        public const string About_Author = nameof(About_Author);
        public const string About_Plugin = nameof(About_Plugin);
        public const string About_PluginDesc = nameof(About_PluginDesc);
        public const string About_Repo = nameof(About_Repo);
        public const string About_Language = nameof(About_Language);

        public const string Mapping_NewType = nameof(Mapping_NewType);
        public const string Mapping_NotSelected = nameof(Mapping_NotSelected);
        public const string Fitting_DirectConnect = nameof(Fitting_DirectConnect);
        public const string Fitting_TypeArrow = nameof(Fitting_TypeArrow);

        // ProjectManagement
        public const string PM_Title_Settings = "PM_Title_Settings";
        public const string PM_Title_Progress = "PM_Title_Progress";
        public const string PM_Tab_General = "PM_Tab_General";
        public const string PM_Tab_Purge = "PM_Tab_Purge";
        public const string PM_Tab_Views = "PM_Tab_Views";
        public const string PM_Tab_Naming = "PM_Tab_Naming";
        public const string PM_CurrentFile = "PM_CurrentFile";
        public const string PM_FilePath = "PM_FilePath";
        public const string PM_Folder = "PM_Folder";
        public const string PM_FileName = "PM_FileName";
        public const string PM_SharedFolder = "PM_SharedFolder";
        public const string PM_Browse = "PM_Browse";
        public const string PM_SyncBefore = "PM_SyncBefore";
        public const string PM_PurgeTitle = "PM_PurgeTitle";
        public const string PM_PurgeRvtLinks = "PM_PurgeRvtLinks";
        public const string PM_PurgeCadImports = "PM_PurgeCadImports";
        public const string PM_PurgeImages = "PM_PurgeImages";
        public const string PM_PurgePointClouds = "PM_PurgePointClouds";
        public const string PM_PurgeGroups = "PM_PurgeGroups";
        public const string PM_PurgeAssemblies = "PM_PurgeAssemblies";
        public const string PM_PurgeSpaces = "PM_PurgeSpaces";
        public const string PM_PurgeRebar = "PM_PurgeRebar";
        public const string PM_PurgeFabric = "PM_PurgeFabric";
        public const string PM_PurgeSheets = "PM_PurgeSheets";
        public const string PM_PurgeSchedules = "PM_PurgeSchedules";
        public const string PM_PurgeUnused = "PM_PurgeUnused";
        public const string PM_SelectAll = "PM_SelectAll";
        public const string PM_DeselectAll = "PM_DeselectAll";
        public const string PM_ViewsTitle = "PM_ViewsTitle";
        public const string PM_RefreshViews = "PM_RefreshViews";
        public const string PM_SearchViews = "PM_SearchViews";
        public const string PM_SelectedCount = "PM_SelectedCount";
        public const string PM_NamingTemplate = "PM_NamingTemplate";
        public const string PM_Delimiter = "PM_Delimiter";
        public const string PM_Blocks = "PM_Blocks";
        public const string PM_StatusMappings = "PM_StatusMappings";
        public const string PM_Preview = "PM_Preview";
        public const string PM_Col_Index = "PM_Col_Index";
        public const string PM_Col_Role = "PM_Col_Role";
        public const string PM_Col_Label = "PM_Col_Label";
        public const string PM_Col_Wip = "PM_Col_Wip";
        public const string PM_Col_Shared = "PM_Col_Shared";
        public const string PM_StatusMarker = "PM_StatusMarker";
        public const string PM_CurrentName = "PM_CurrentName";
        public const string PM_SharedName = "PM_SharedName";
        public const string PM_AddBlock = "PM_AddBlock";
        public const string PM_RemoveBlock = "PM_RemoveBlock";
        public const string PM_AddMapping = "PM_AddMapping";
        public const string PM_RemoveMapping = "PM_RemoveMapping";
        public const string PM_ParseFromName = "PM_ParseFromName";
        public const string PM_Step_Validate = "PM_Step_Validate";
        public const string PM_Step_Sync = "PM_Step_Sync";
        public const string PM_Step_TempProject = "PM_Step_TempProject";
        public const string PM_Step_Detach = "PM_Step_Detach";
        public const string PM_Step_Purge = "PM_Step_Purge";
        public const string PM_Step_Save = "PM_Step_Save";
        public const string PM_Step_Finish = "PM_Step_Finish";
        public const string PM_Result_NoSettings = "PM_Result_NoSettings";
    }
}

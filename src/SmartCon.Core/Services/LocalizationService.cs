using System.Text.Json;

namespace SmartCon.Core.Services;

public static class LocalizationService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon", "settings.json");

    private static readonly Dictionary<string, string> Ru = new()
    {
        ["App_Name"] = "SmartCon",
        ["Btn_Cancel"] = "Отмена",
        ["Btn_Connect"] = "Соединить",
        ["Btn_Insert"] = "Вставить",
        ["Btn_Change"] = "Изменить",
        ["Btn_Save"] = "Сохранить",
        ["Btn_Saved"] = "✓ Сохранено",
        ["Btn_Delete"] = "Удалить",
        ["Btn_Add"] = "+ Добавить",
        ["Btn_Confirm"] = "Подтвердить",
        ["Btn_Close"] = "Закрыть",
        ["Btn_OK"] = "OK",

        ["Title_EditorSetup"] = "Настройка соединения элементов",
        ["Heading_EditorSetup"] = "Настройка соединения элементов",
        ["Title_Settings"] = "Настройки SmartCon",
        ["Title_FamilySelector"] = "Выбор семейств фитингов",
        ["Title_ConnType"] = "Тип соединения",
        ["Title_CtcSetup"] = "Назначение типов коннекторов",
        ["Title_About"] = "О SmartCon",

        ["Tip_RotateCCW"] = "Повернуть против часовой стрелки",
        ["Tip_RotationAngle"] = "Угол поворота в градусах",
        ["Tip_RotateCW"] = "Повернуть по часовой стрелке",
        ["Tip_AvailableSizes"] = "Доступные типоразмеры динамического семейства",
        ["Tip_ChangeSize"] = "Изменить размер динамического семейства",
        ["Tip_FittingCtc"] = "Отразить типы коннекторов фитинга",
        ["Tip_SelectFitting"] = "Выберите фитинг для соединения",
        ["Tip_InsertFitting"] = "Вставить выбранный фитинг (примерка)",
        ["Tip_ReducerCtc"] = "Отразить типы коннекторов переходника",
        ["Tip_SelectReducer"] = "Выберите переходник",
        ["Tip_InsertReducer"] = "Вставить выбранный переходник",
        ["Tip_ChainDecrement"] = "Отсоединить последний уровень",
        ["Tip_ChainIncrement"] = "Присоединить следующий уровень",
        ["Tip_ConnectAll"] = "Подключить все элементы сети сразу",
        ["Tip_SelectFittingFamilies"] = "Нажмите для выбора семейств фитингов (недоступно при «Прямое» — используйте переходы сечения)",
        ["Tip_SelectReducerFamilies"] = "Нажмите для выбора семейств переходников (недоступно при непрямом — используйте фитинги)",
        ["Tip_ChangeConnection"] = "Переключить на другой свободный коннектор",
        ["Tip_Connect"] = "Выполнить соединение элементов",

        ["Label_Size"] = "Размер:",
        ["Label_Network"] = "Сеть:",
        ["Label_Updates"] = "Обновления",
        ["Label_AvailableFamilies"] = "Доступные семейства:",
        ["Label_FamilyFilter"] = "(OST_PipeFitting, MultiPort, 2 коннектора)",
        ["Label_SelectedFamilies"] = "Выбранные (порядок = приоритет):",
        ["Label_Family"] = "Семейство: ",
        ["Label_Symbol"] = "Типоразмер: ",
        ["Label_Connector"] = "Коннектор ",

        ["Btn_ChangeConnection"] = "Изменить соединение",
        ["Btn_ConnectAll"] = "Подключить всё",
        ["Btn_AddFamily"] = "▶▶ Добавить",
        ["Btn_RemoveFamily"] = "◀◀ Убрать",
        ["Btn_Up"] = "▲ Вверх",
        ["Btn_Down"] = "▼ Вниз",
        ["Btn_CheckUpdates"] = "Проверить обновления",
        ["Btn_DownloadInstall"] = "Скачать и установить",
        ["Chk_CheckOnStartup"] = "Проверять при запуске",

        ["Tab_ConnectorTypes"] = "Типы коннекторов",
        ["Tab_MappingRules"] = "Правила маппинга",

        ["Col_Code"] = "Код",
        ["Col_Name"] = "Название",
        ["Col_Description"] = "Описание",
        ["Col_ConnType1"] = "Тип соединения 1",
        ["Col_ConnType2"] = "Тип соединения 2",
        ["Col_Direct"] = "Прямое",
        ["Col_Fittings"] = "Фитинги",
        ["Col_Transitions"] = "Переходы сечения",

        ["Status_Active"] = "Активно",
        ["Status_Processing"] = "Обработка…",
        ["Status_SessionEnded"] = "Сессия завершена",
        ["Status_Rotated"] = "Повернуто на {0}°",
        ["Status_Initializing"] = "Инициализация…",
        ["Status_InsertingFitting"] = "Установка фитинга…",
        ["Status_ReadyToConnect"] = "Готово к соединению",
        ["Status_InsertingReducer"] = "Вставка переходника…",
        ["Status_SwitchingConnector"] = "Переключение коннектора…",
        ["Status_ConnectorChanged"] = "Коннектор изменён",
        ["Status_ChangingSizeTo"] = "Изменение размера на {0}…",
        ["Status_SizeChangedTo"] = "Размер изменён на {0}",

        ["Status_UpdatingFitting"] = "Обновление фитинга…",
        ["Status_Validating"] = "Проверка и финальная подгонка…",
        ["Status_SizingReducer"] = "Подбор размера переходника…",
        ["Status_WritingCtc"] = "Запись типов коннекторов…",
        ["Status_Connected"] = "Соединение выполнено",
        ["Status_ReducerSet"] = "Переходник: {0}",
        ["Status_NoReducerData"] = "Нет данных о семействе переходника",
        ["Status_FamilyNotFound"] = "Семейство '{0}' не найдено",
        ["Status_CtcReducerUpdated"] = "CTC переходника отражён — переориентирован",
        ["Status_CtcFittingUpdated"] = "CTC фитинга отражён — переориентирован",
        ["Status_CtcReflected"] = "Типы коннекторов отражены",
        ["Status_DirectConnect"] = "Прямое соединение",
        ["Status_NoFittingData"] = "Нет данных о семействе фитинга",
        ["Status_Inserted"] = "Вставлен: {0}",
        ["Status_FamilyNotFoundInProject"] = "Семейство '{0}' не найдено в проекте",
        ["Status_InsertingFittingAction"] = "Вставка фитинга…",
        ["Status_AttachingLevel"] = "Присоединение уровня {0}…",
        ["Status_LevelAttached"] = "Уровень {0} присоединён",
        ["Status_RollbackLevel"] = "Откат уровня {0}…",
        ["Status_LevelDetached"] = "Уровень {0} отсоединён",
        ["Status_ConnectingNetwork"] = "Подключение всей сети…",
        ["Status_LevelsConnected"] = "Подключено {0} уровней",
        ["Lbl_NoChain"] = "нет цепочки",

        ["Pick_FirstElement"] = "PipeConnect: выберите ПЕРВЫЙ элемент (будет присоединён)",
        ["Pick_SecondElement"] = "PipeConnect: выберите ВТОРОЙ элемент (неподвижный ориентир)",

        ["Msg_NoConnectorsFirst"] = "Нет свободных коннекторов у первого элемента.",
        ["Msg_NoConnectorsSecond"] = "Нет свободных коннекторов у второго элемента.",
        ["Msg_ConfigureTypes"] = "Сначала настройте типы коннекторов в Настройках.",
        ["Msg_AssignCtc"] = "У фитинга не заданы типы коннекторов. Назначьте тип каждому коннектору:",
        ["Msg_CtcNotAssigned"] = "Типы коннекторов не назначены. Фитинг не будет вставлен.",
        ["Msg_ReducerCtcNotAssigned"] = "Типы коннекторов не назначены. Переходник не будет вставлен.",

        ["Error_Init"] = "Ошибка инициализации: {0}",
        ["Error_Rotate"] = "Ошибка поворота: {0}",
        ["Error_ChangeSize"] = "Ошибка смены размера: {0}",
        ["Error_Insert"] = "Ошибка вставки: {0}",
        ["Error_Chain"] = "Ошибка цепочки: {0}",
        ["Error_Rollback"] = "Ошибка отката: {0}",
        ["Error_General"] = "Ошибка: {0}",

        ["Tx_PipeConnect"] = "PipeConnect",
        ["Tx_InsertReducer"] = "PipeConnect — Вставка reducer",
        ["Tx_InsertTransition"] = "PipeConnect — Вставка перехода",
        ["Tx_PositionAfterReducer"] = "PipeConnect — Позиция dynamic после reducer resize",
        ["Tx_PositionAfterReducerReSize"] = "PipeConnect — Позиция dynamic после reducer re-size",
        ["Tx_DirectConnect"] = "PipeConnect — Прямое соединение",
        ["Tx_InsertFitting"] = "PipeConnect — Вставка фитинга",
        ["Tx_ReorientReducer"] = "PipeConnect — Переориентация reducer",
        ["Tx_ReorientFitting"] = "PipeConnect — Переориентация фитинга",
        ["Tx_ReflectCtc"] = "PipeConnect — Отражение CTC",
        ["Tx_SwitchConnector"] = "PipeConnect — Смена коннектора",
        ["Tx_ChangeSizeDynamic"] = "PipeConnect — Смена размера dynamic",
        ["Tx_Rotate"] = "PipeConnect — Поворот",
        ["Tx_AdjustSize"] = "PipeConnect — Подгонка размера",
        ["Tx_Disconnect"] = "PipeConnect — Отсоединение",
        ["Tx_Align"] = "PipeConnect — Выравнивание",
        ["Tx_SetCtc"] = "PipeConnect — SetConnectorType",
        ["Tx_FinalAdjustment"] = "PipeConnect — Финальная корректировка",
        ["Tx_ConnectTo"] = "PipeConnect — ConnectTo",
        ["Tx_FittingSize"] = "PipeConnect — Размер фитинга",
        ["Tx_FitDynamicToFitting"] = "PipeConnect — Подгонка dynamic под фитинг",
        ["Tx_AlignAfterSize"] = "PipeConnect — Выравнивание после размера",
        ["Tx_ChainLevel"] = "Цепочка: уровень {0}",
        ["Tx_ChainRollback"] = "Цепочка: откат уровня {0}",
        ["Chain_Levels"] = "уровней",

        ["About_Version"] = "Версия {0}",
        ["About_Author"] = "Автор:",
        ["About_Plugin"] = "Плагин:",
        ["About_PluginDesc"] = "MEP коннектор труб для Revit 2025",
        ["About_Repo"] = "Репозиторий:",
        ["About_Language"] = "Язык:",
        ["About_CheckingUpdates"] = "Проверка обновлений…",
        ["About_UpToDate"] = "v{0} — актуальная версия.",
        ["About_CurrentLatest"] = "Текущая: v{0} (последняя)",
        ["About_AvailableVersion"] = "Доступна: v{0} (текущая: v{1})",
        ["About_VersionAvailable"] = "v{0} доступна. Нажмите «Скачать и установить».",
        ["About_Downloading"] = "Загрузка…",
        ["About_WillInstallOnClose"] = "v{0} будет установлена при закрытии Revit.",
        ["About_DownloadError"] = "Ошибка загрузки: {0}",
        ["About_PendingUpdate"] = "Ожидаемое обновление будет установлено при закрытии Revit.",

        ["Mapping_NewType"] = "Новый тип",
        ["Mapping_SaveError"] = "Ошибка сохранения SmartCon",
        ["Mapping_NotSelected"] = "(не выбраны)",
        ["Mapping_ImportTitle"] = "Импорт маппинга из JSON",
        ["Mapping_ExportTitle"] = "Экспорт маппинга в JSON",
        ["Mapping_ExportDefaultFileName"] = "smartcon-mapping.json",
        ["Mapping_ImportFailed"] = "Не удалось прочитать файл: неверный формат или файл повреждён.",
        ["Mapping_ImportErrorTitle"] = "Ошибка импорта",
        ["Fitting_DirectConnect"] = "Без фитинга (прямое соединение)",
        ["Fitting_ReducerSuffix"] = "🔧 {0} (переход)",
        ["Fitting_TypeArrow"] = "Тип {0} → {1}",

        ["Warn_SizeNotExactUnconstrained"] = "Размер DN{0} не найден точно. Ближайший DN{1} (other connectors will change).",
        ["Warn_SizeNotInTable"] = "Размер DN{0} отсутствует в таблице. Будет выбран DN{1}, нужен переходник.",
        ["Warn_NoSizeParameter"] = "Не удалось определить параметр размера. Будет вставлен переходник если настроен в маппинге.",

        ["PM_Col_Index"] = "№",
        ["PM_Col_Role"] = "Роль",
        ["PM_Col_Label"] = "Метка",
        ["PM_Col_Wip"] = "WIP",
        ["PM_Col_Shared"] = "Shared",
        ["PM_Title_Settings"] = "Настройки шаринга",
        ["PM_Title_Progress"] = "Шаринг проекта",
        ["PM_Result_Success"] = "Проект успешно перемещён в зону Shared.",
        ["PM_Result_Failed"] = "Ошибка шаринга: {0}",
        ["PM_Result_NoSettings"] = "Сначала настройте параметры шаринга через кнопку Settings.",
        ["PM_Result_InvalidName"] = "Имя файла не соответствует шаблону: {0}",
        ["PM_Result_SyncFailed"] = "Синхронизация не удалась: {0}",
        ["PM_Step_Validate"] = "Проверяем настройки...",
        ["PM_Step_Sync"] = "Синхронизируем ваш проект...",
        ["PM_Step_TempProject"] = "Создаём временный проект...",
        ["PM_Step_Detach"] = "Открываем с отсоединением от ФХ...",
        ["PM_Step_Purge"] = "Очищаем модель...",
        ["PM_Step_Save"] = "Сохраняем в зону Shared...",
        ["PM_Step_Finish"] = "Завершение...",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["App_Name"] = "SmartCon",
        ["Btn_Cancel"] = "Cancel",
        ["Btn_Connect"] = "Connect",
        ["Btn_Insert"] = "Insert",
        ["Btn_Change"] = "Change",
        ["Btn_Save"] = "Save",
        ["Btn_Saved"] = "✓ Saved",
        ["Btn_Delete"] = "Delete",
        ["Btn_Add"] = "+ Add",
        ["Btn_Confirm"] = "Confirm",
        ["Btn_Close"] = "Close",
        ["Btn_OK"] = "OK",

        ["Title_EditorSetup"] = "Connection Setup",
        ["Heading_EditorSetup"] = "Connection Setup",
        ["Title_Settings"] = "SmartCon Settings",
        ["Title_FamilySelector"] = "Select Fitting Families",
        ["Title_ConnType"] = "Connection Type",
        ["Title_CtcSetup"] = "Assign Connector Types",
        ["Title_About"] = "About SmartCon",

        ["Tip_RotateCCW"] = "Rotate counter-clockwise",
        ["Tip_RotationAngle"] = "Rotation angle in degrees",
        ["Tip_RotateCW"] = "Rotate clockwise",
        ["Tip_AvailableSizes"] = "Available dynamic family sizes",
        ["Tip_ChangeSize"] = "Change dynamic family size",
        ["Tip_FittingCtc"] = "Reflect fitting connector types",
        ["Tip_SelectFitting"] = "Select fitting for connection",
        ["Tip_InsertFitting"] = "Insert selected fitting (preview)",
        ["Tip_ReducerCtc"] = "Reflect reducer connector types",
        ["Tip_SelectReducer"] = "Select reducer",
        ["Tip_InsertReducer"] = "Insert selected reducer",
        ["Tip_ChainDecrement"] = "Disconnect last level",
        ["Tip_ChainIncrement"] = "Connect next level",
        ["Tip_ConnectAll"] = "Connect all network elements at once",
        ["Tip_SelectFittingFamilies"] = "Click to select fitting families (unavailable for Direct — use cross-section transitions)",
        ["Tip_SelectReducerFamilies"] = "Click to select reducer families",
        ["Tip_ChangeConnection"] = "Switch to another free connector",
        ["Tip_Connect"] = "Execute element connection",

        ["Label_Size"] = "Size:",
        ["Label_Network"] = "Network:",
        ["Label_Updates"] = "Updates",
        ["Label_AvailableFamilies"] = "Available families:",
        ["Label_FamilyFilter"] = "(OST_PipeFitting, MultiPort, 2 connectors)",
        ["Label_SelectedFamilies"] = "Selected (order = priority):",
        ["Label_Family"] = "Family: ",
        ["Label_Symbol"] = "Type: ",
        ["Label_Connector"] = "Connector ",

        ["Btn_ChangeConnection"] = "Change connection",
        ["Btn_ConnectAll"] = "Connect all",
        ["Btn_AddFamily"] = "▶▶ Add",
        ["Btn_RemoveFamily"] = "◀◀ Remove",
        ["Btn_Up"] = "▲ Up",
        ["Btn_Down"] = "▼ Down",
        ["Btn_CheckUpdates"] = "Check for updates",
        ["Btn_DownloadInstall"] = "Download and install",
        ["Chk_CheckOnStartup"] = "Check on startup",

        ["Tab_ConnectorTypes"] = "Connector Types",
        ["Tab_MappingRules"] = "Mapping Rules",

        ["Col_Code"] = "Code",
        ["Col_Name"] = "Name",
        ["Col_Description"] = "Description",
        ["Col_ConnType1"] = "Connection Type 1",
        ["Col_ConnType2"] = "Connection Type 2",
        ["Col_Direct"] = "Direct",
        ["Col_Fittings"] = "Fittings",
        ["Col_Transitions"] = "Cross-Section Transitions",

        ["Status_Active"] = "Active",
        ["Status_Processing"] = "Processing…",
        ["Status_SessionEnded"] = "Session ended",
        ["Status_Rotated"] = "Rotated by {0}°",
        ["Status_Initializing"] = "Initializing…",
        ["Status_InsertingFitting"] = "Inserting fitting…",
        ["Status_ReadyToConnect"] = "Ready to connect",
        ["Status_InsertingReducer"] = "Inserting reducer…",
        ["Status_SwitchingConnector"] = "Switching connector…",
        ["Status_ConnectorChanged"] = "Connector changed",
        ["Status_ChangingSizeTo"] = "Changing size to {0}…",
        ["Status_SizeChangedTo"] = "Size changed to {0}",

        ["Status_UpdatingFitting"] = "Updating fitting…",
        ["Status_Validating"] = "Validating and final fitting…",
        ["Status_SizingReducer"] = "Sizing reducer…",
        ["Status_WritingCtc"] = "Writing connector types…",
        ["Status_Connected"] = "Connection completed",
        ["Status_ReducerSet"] = "Reducer: {0}",
        ["Status_NoReducerData"] = "No reducer family data",
        ["Status_FamilyNotFound"] = "Family '{0}' not found",
        ["Status_CtcReducerUpdated"] = "Reducer CTC reflected — reoriented",
        ["Status_CtcFittingUpdated"] = "Fitting CTC reflected — reoriented",
        ["Status_CtcReflected"] = "Connector types reflected",
        ["Status_DirectConnect"] = "Direct connect",
        ["Status_NoFittingData"] = "No fitting family data",
        ["Status_Inserted"] = "Inserted: {0}",
        ["Status_FamilyNotFoundInProject"] = "Family '{0}' not found in project",
        ["Status_InsertingFittingAction"] = "Inserting fitting…",
        ["Status_AttachingLevel"] = "Attaching level {0}…",
        ["Status_LevelAttached"] = "Level {0} attached",
        ["Status_RollbackLevel"] = "Rolling back level {0}…",
        ["Status_LevelDetached"] = "Level {0} detached",
        ["Status_ConnectingNetwork"] = "Connecting entire network…",
        ["Status_LevelsConnected"] = "{0} levels connected",
        ["Lbl_NoChain"] = "no chain",

        ["Pick_FirstElement"] = "PipeConnect: select FIRST element (to be connected)",
        ["Pick_SecondElement"] = "PipeConnect: select SECOND element (fixed reference)",

        ["Msg_NoConnectorsFirst"] = "No free connectors on first element.",
        ["Msg_NoConnectorsSecond"] = "No free connectors on second element.",
        ["Msg_ConfigureTypes"] = "Configure connector types in Settings first.",
        ["Msg_AssignCtc"] = "Fitting has no connector types assigned. Assign a type to each connector:",
        ["Msg_CtcNotAssigned"] = "Connector types not assigned. Fitting will not be inserted.",
        ["Msg_ReducerCtcNotAssigned"] = "Connector types not assigned. Reducer will not be inserted.",

        ["Error_Init"] = "Initialization error: {0}",
        ["Error_Rotate"] = "Rotation error: {0}",
        ["Error_ChangeSize"] = "Size change error: {0}",
        ["Error_Insert"] = "Insert error: {0}",
        ["Error_Chain"] = "Chain error: {0}",
        ["Error_Rollback"] = "Rollback error: {0}",
        ["Error_General"] = "Error: {0}",

        ["Tx_PipeConnect"] = "PipeConnect",
        ["Tx_InsertReducer"] = "PipeConnect — Insert reducer",
        ["Tx_InsertTransition"] = "PipeConnect — Insert transition",
        ["Tx_PositionAfterReducer"] = "PipeConnect — Dynamic position after reducer resize",
        ["Tx_PositionAfterReducerReSize"] = "PipeConnect — Dynamic position after reducer re-size",
        ["Tx_DirectConnect"] = "PipeConnect — Direct connect",
        ["Tx_InsertFitting"] = "PipeConnect — Insert fitting",
        ["Tx_ReorientReducer"] = "PipeConnect — Reorient reducer",
        ["Tx_ReorientFitting"] = "PipeConnect — Reorient fitting",
        ["Tx_ReflectCtc"] = "PipeConnect — Reflect CTC",
        ["Tx_SwitchConnector"] = "PipeConnect — Switch connector",
        ["Tx_ChangeSizeDynamic"] = "PipeConnect — Change dynamic size",
        ["Tx_Rotate"] = "PipeConnect — Rotation",
        ["Tx_AdjustSize"] = "PipeConnect — Adjust size",
        ["Tx_Disconnect"] = "PipeConnect — Disconnect",
        ["Tx_Align"] = "PipeConnect — Alignment",
        ["Tx_SetCtc"] = "PipeConnect — SetConnectorType",
        ["Tx_FinalAdjustment"] = "PipeConnect — Final adjustment",
        ["Tx_ConnectTo"] = "PipeConnect — ConnectTo",
        ["Tx_FittingSize"] = "PipeConnect — Fitting size",
        ["Tx_FitDynamicToFitting"] = "PipeConnect — Fit dynamic to fitting",
        ["Tx_AlignAfterSize"] = "PipeConnect — Alignment after size",
        ["Tx_ChainLevel"] = "Chain: level {0}",
        ["Tx_ChainRollback"] = "Chain: rollback level {0}",
        ["Chain_Levels"] = "levels",

        ["About_Version"] = "Version {0}",
        ["About_Author"] = "Author:",
        ["About_Plugin"] = "Plugin:",
        ["About_PluginDesc"] = "MEP Pipe Connector for Revit 2025",
        ["About_Repo"] = "Repo:",
        ["About_Language"] = "Language:",
        ["About_CheckingUpdates"] = "Checking for updates…",
        ["About_UpToDate"] = "v{0} is up to date.",
        ["About_CurrentLatest"] = "Current: v{0} (latest)",
        ["About_AvailableVersion"] = "Available: v{0} (current: v{1})",
        ["About_VersionAvailable"] = "v{0} available. Click Download.",
        ["About_Downloading"] = "Downloading…",
        ["About_WillInstallOnClose"] = "v{0} will be installed when Revit closes.",
        ["About_DownloadError"] = "Download error: {0}",
        ["About_PendingUpdate"] = "Pending update will be installed when Revit closes.",

        ["Mapping_NewType"] = "New type",
        ["Mapping_SaveError"] = "SmartCon save error",
        ["Mapping_NotSelected"] = "(not selected)",
        ["Mapping_ImportTitle"] = "Import mapping from JSON",
        ["Mapping_ExportTitle"] = "Export mapping to JSON",
        ["Mapping_ExportDefaultFileName"] = "smartcon-mapping.json",
        ["Mapping_ImportFailed"] = "Failed to read the file: invalid format or corrupted.",
        ["Mapping_ImportErrorTitle"] = "Import error",
        ["Fitting_DirectConnect"] = "No fitting (direct connect)",
        ["Fitting_ReducerSuffix"] = "🔧 {0} (transition)",
        ["Fitting_TypeArrow"] = "Type {0} → {1}",

        ["Warn_SizeNotExactUnconstrained"] = "Size DN{0} not found exactly. Nearest DN{1} (other connectors will change).",
        ["Warn_SizeNotInTable"] = "Size DN{0} not in table. Nearest DN{1} will be used, reducer needed.",
        ["Warn_NoSizeParameter"] = "Could not determine size parameter. Reducer will be inserted if configured in mapping.",

        ["PM_Col_Index"] = "#",
        ["PM_Col_Role"] = "Role",
        ["PM_Col_Label"] = "Label",
        ["PM_Col_Wip"] = "WIP",
        ["PM_Col_Shared"] = "Shared",
        ["PM_Title_Settings"] = "Share Settings",
        ["PM_Title_Progress"] = "Sharing Project",
        ["PM_Result_Success"] = "Project shared successfully.",
        ["PM_Result_Failed"] = "Share failed: {0}",
        ["PM_Result_NoSettings"] = "Configure share settings first via the Settings button.",
        ["PM_Result_InvalidName"] = "File name does not match template: {0}",
        ["PM_Result_SyncFailed"] = "Synchronization failed: {0}",
        ["PM_Step_Validate"] = "Validating settings...",
        ["PM_Step_Sync"] = "Synchronizing your project...",
        ["PM_Step_TempProject"] = "Creating temporary project...",
        ["PM_Step_Detach"] = "Detaching from central...",
        ["PM_Step_Purge"] = "Purging model...",
        ["PM_Step_Save"] = "Saving to Shared folder...",
        ["PM_Step_Finish"] = "Finalizing...",
    };

    private static Dictionary<string, string> _current = Ru;

    public static Language CurrentLanguage { get; private set; } = Language.RU;

    public static event Action? LanguageChanged;

    public static string GetString(string key)
        => _current.TryGetValue(key, out var value) ? value : key;

    public static void SetLanguage(Language lang)
    {
        if (CurrentLanguage == lang) return;
        CurrentLanguage = lang;
        _current = lang == Language.EN ? En : Ru;
        LanguageChanged?.Invoke();
    }

    public static void LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var doc = JsonDocument.Parse(json);
            var langStr = doc.RootElement.TryGetProperty("language", out var prop)
                ? prop.GetString() : "RU";
            var lang = langStr == "EN" ? Language.EN : Language.RU;
            CurrentLanguage = lang;
            _current = lang == Language.EN ? En : Ru;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SmartConLogger] {ex.Message}");
            CurrentLanguage = Language.RU;
            _current = Ru;
        }
    }

    public static void SaveLanguage()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = "{\"language\":\"" + (CurrentLanguage == Language.EN ? "EN" : "RU") + "\"}";
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] {ex.Message}"); }
    }

    public static string Get(Language lang)
        => lang == Language.EN ? "EN" : "RU";
}

using System.Windows;
using SmartCon.Core.Services;

namespace SmartCon.PipeConnect.Services;

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
    }
}

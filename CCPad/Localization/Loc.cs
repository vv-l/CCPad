using System;
using System.Collections.Generic;
using System.Globalization;
using CCPad.Settings;

namespace CCPad.Localization
{
    /// <summary>
    /// Lightweight UI string table. CCPad builds most of its UI imperatively in
    /// code-behind, so the standard .resw/x:Uid pipeline can't reach it; this
    /// central dictionary does instead. Look up with <see cref="T(string)"/> (or
    /// the format overload). Switching language is live: change the language and
    /// the menus/tooltips that subscribe to <see cref="LanguageChanged"/> rebuild,
    /// while dialogs/toasts/banners pick up the new language the next time they're
    /// shown (they're created on demand).
    ///
    /// Each entry is a string[] whose columns follow <see cref="_order"/>. An empty
    /// cell falls back to English, then to zh-Hans.
    /// </summary>
    public static class Loc
    {
        public const string ZhHans = "zh-Hans";
        public const string En = "en";
        public const string ZhHant = "zh-Hant";
        public const string De = "de";
        public const string Ja = "ja";
        public const string Fr = "fr";
        public const string Ko = "ko";
        public const string Es = "es";
        public const string It = "it";

        /// <summary>Column order of every value array in <see cref="_t"/>.</summary>
        private static readonly string[] _order = { ZhHans, En, ZhHant, De, Ja, Fr, Ko, Es, It };

        /// <summary>Order the language picker lists them in (English first, per design).</summary>
        public static readonly string[] PickerOrder = { En, ZhHans, ZhHant, De, Ja, Fr, Ko, Es, It };

        /// <summary>Raised after the language changes so UI can rebuild itself.</summary>
        public static event Action? LanguageChanged;

        private static string _lang = ZhHans;
        public static string Lang => _lang;

        /// <summary>Load the saved language, or detect from the OS on first run.</summary>
        public static void Init()
        {
            var saved = AppConfig.Load().Language;
            _lang = string.IsNullOrEmpty(saved) ? DetectOsLang() : Normalize(saved);
        }

        public static void SetLanguage(string lang, bool persist = true)
        {
            lang = Normalize(lang);
            if (lang == _lang) return;
            _lang = lang;
            if (persist)
            {
                var prefs = AppConfig.Load();
                prefs.Language = lang;
                AppConfig.Save(prefs);
            }
            try { LanguageChanged?.Invoke(); } catch { }
        }

        public static string Normalize(string? lang) => lang switch
        {
            En => En,
            ZhHant => ZhHant,
            De => De,
            Ja => Ja,
            Fr => Fr,
            Ko => Ko,
            Es => Es,
            It => It,
            _ => ZhHans,
        };

        /// <summary>Native display name for a language code (for the picker).</summary>
        public static string DisplayName(string lang) => Normalize(lang) switch
        {
            En => "English",
            ZhHant => "繁體中文",
            De => "Deutsch",
            Ja => "日本語",
            Fr => "Français",
            Ko => "한국어",
            Es => "Español",
            It => "Italiano",
            _ => "简体中文",
        };

        private static string DetectOsLang()
        {
            try
            {
                var name = CultureInfo.CurrentUICulture.Name; // e.g. zh-CN, ja-JP, de-DE
                if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("MO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0)
                        return ZhHant;
                    return ZhHans;
                }
                var two = (name.Length >= 2 ? name.Substring(0, 2) : name).ToLowerInvariant();
                return two switch
                {
                    "de" => De,
                    "ja" => Ja,
                    "fr" => Fr,
                    "ko" => Ko,
                    "es" => Es,
                    "it" => It,
                    "en" => En,
                    _ => En,
                };
            }
            catch { return ZhHans; }
        }

        /// <summary>Look up a string in the current language (falls back to English, then zh-Hans).</summary>
        public static string T(string key)
        {
            if (_t.TryGetValue(key, out var row))
            {
                int idx = Array.IndexOf(_order, _lang);
                if (idx < 0) idx = 0;
                string val = idx < row.Length ? row[idx] : "";
                if (string.IsNullOrEmpty(val) && row.Length > 1) val = row[1]; // → English
                if (string.IsNullOrEmpty(val)) val = row[0];                   // → zh-Hans
                return val;
            }
            return key; // missing key — surface it so it's obvious in dev
        }

        /// <summary>Look up + string.Format with args.</summary>
        public static string T(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }

        // Columns, in order: zh-Hans, en, zh-Hant, de, ja, fr, ko, es, it
        private static readonly Dictionary<string, string[]> _t = new()
        {
            // ── Common buttons ──
            ["ok"] = new[] { "确定", "OK", "確定", "OK", "OK", "OK", "확인", "Aceptar", "OK" },
            ["cancel"] = new[] { "取消", "Cancel", "取消", "Abbrechen", "キャンセル", "Annuler", "취소", "Cancelar", "Annulla" },
            ["close"] = new[] { "关闭", "Close", "關閉", "Schließen", "閉じる", "Fermer", "닫기", "Cerrar", "Chiudi" },
            ["later"] = new[] { "稍后", "Later", "稍後", "Später", "後で", "Plus tard", "나중에", "Más tarde", "Più tardi" },

            // ── Session recovery ──
            ["recovery_dontask"] = new[] { "以后不再询问，直接全新开始", "Don't ask again; always start fresh", "以後不再詢問，直接全新開始", "Nicht mehr fragen; immer neu starten", "今後確認せず、常に新規で開始", "Ne plus demander ; toujours démarrer à neuf", "다시 묻지 않고 항상 새로 시작", "No volver a preguntar; empezar siempre de nuevo", "Non chiedere più; ricomincia sempre da capo" },
            ["recovery_body"] = new[] { "上次会话似乎是异常退出的。是否恢复之前的窗口布局？", "The last session looks like it exited abnormally. Restore the previous window layout?", "上次的工作階段似乎是異常結束的。是否還原先前的視窗配置？", "Die letzte Sitzung wurde anscheinend unerwartet beendet. Vorheriges Fensterlayout wiederherstellen?", "前回のセッションは異常終了したようです。以前のウィンドウ レイアウトを復元しますか？", "La session précédente semble s'être fermée anormalement. Restaurer la disposition des fenêtres précédente ?", "지난 세션이 비정상적으로 종료된 것 같습니다. 이전 창 레이아웃을 복원하시겠습니까?", "Parece que la última sesión se cerró de forma anómala. ¿Restaurar la disposición de ventanas anterior?", "La sessione precedente sembra essersi chiusa in modo anomalo. Ripristinare il layout delle finestre precedente?" },
            ["recovery_note"] = new[] { "注意：仅恢复标签和工作目录，终端内的对话历史无法恢复。", "Note: only tabs and working directories are restored — in-terminal conversation history can't be recovered.", "注意：僅還原分頁與工作目錄，終端機內的對話記錄無法還原。", "Hinweis: Nur Tabs und Arbeitsverzeichnisse werden wiederhergestellt – der Konversationsverlauf im Terminal kann nicht wiederhergestellt werden.", "注意：復元されるのはタブと作業ディレクトリのみです。ターミナル内の会話履歴は復元できません。", "Remarque : seuls les onglets et les répertoires de travail sont restaurés — l'historique des conversations du terminal ne peut pas être récupéré.", "참고: 탭과 작업 디렉터리만 복원되며 터미널 내 대화 기록은 복구할 수 없습니다.", "Nota: solo se restauran las pestañas y los directorios de trabajo; el historial de conversación del terminal no se puede recuperar.", "Nota: vengono ripristinati solo le schede e le directory di lavoro; la cronologia delle conversazioni nel terminale non può essere recuperata." },
            ["recovery_title"] = new[] { "恢复上次会话？", "Restore last session?", "還原上次的工作階段？", "Letzte Sitzung wiederherstellen?", "前回のセッションを復元しますか？", "Restaurer la dernière session ?", "지난 세션을 복원하시겠습니까?", "¿Restaurar la última sesión?", "Ripristinare l'ultima sessione?" },
            ["recovery_restore"] = new[] { "恢复", "Restore", "還原", "Wiederherstellen", "復元", "Restaurer", "복원", "Restaurar", "Ripristina" },
            ["recovery_fresh"] = new[] { "全新开始", "Start fresh", "全新開始", "Neu starten", "新規開始", "Démarrer à neuf", "새로 시작", "Empezar de nuevo", "Ricomincia da capo" },

            // ── Workspace ──
            ["ws_save"] = new[] { "保存工作区", "Save workspace", "儲存工作區", "Arbeitsbereich speichern", "ワークスペースを保存", "Enregistrer l'espace de travail", "작업 영역 저장", "Guardar espacio de trabajo", "Salva area di lavoro" },
            ["ws_saveas"] = new[] { "保存工作区为...", "Save workspace as…", "另存工作區…", "Arbeitsbereich speichern unter…", "ワークスペースを名前を付けて保存…", "Enregistrer l'espace de travail sous…", "다른 이름으로 작업 영역 저장…", "Guardar espacio de trabajo como…", "Salva area di lavoro con nome…" },
            ["ws_open"] = new[] { "打开工作区...", "Open workspace…", "開啟工作區…", "Arbeitsbereich öffnen…", "ワークスペースを開く…", "Ouvrir un espace de travail…", "작업 영역 열기…", "Abrir espacio de trabajo…", "Apri area di lavoro…" },
            ["ws_filetype"] = new[] { "CCPad 工作区", "CCPad workspace", "CCPad 工作區", "CCPad-Arbeitsbereich", "CCPad ワークスペース", "Espace de travail CCPad", "CCPad 작업 영역", "Espacio de trabajo CCPad", "Area di lavoro CCPad" },

            // ── About menu ──
            ["menu_check_update"] = new[] { "检查更新", "Check for updates", "檢查更新", "Nach Updates suchen", "更新を確認", "Rechercher des mises à jour", "업데이트 확인", "Buscar actualizaciones", "Controlla aggiornamenti" },
            ["menu_remote"] = new[] { "远程终端", "Remote terminal", "遠端終端機", "Remote-Terminal", "リモート ターミナル", "Terminal distant", "원격 터미널", "Terminal remoto", "Terminale remoto" },
            ["menu_remote_running"] = new[] { "远程终端 (运行中)", "Remote terminal (running)", "遠端終端機 (執行中)", "Remote-Terminal (läuft)", "リモート ターミナル (実行中)", "Terminal distant (en cours)", "원격 터미널 (실행 중)", "Terminal remoto (en ejecución)", "Terminale remoto (in esecuzione)" },
            ["menu_recovery_toggle"] = new[] { "异常退出时恢复会话", "Restore session after abnormal exit", "異常結束時還原工作階段", "Sitzung nach unerwartetem Beenden wiederherstellen", "異常終了時にセッションを復元", "Restaurer la session après une fermeture anormale", "비정상 종료 후 세션 복원", "Restaurar la sesión tras un cierre anómalo", "Ripristina la sessione dopo una chiusura anomala" },
            ["menu_clear_recovery"] = new[] { "清除会话恢复记录", "Clear session-recovery snapshot", "清除工作階段還原記錄", "Sitzungswiederherstellungs-Snapshot löschen", "セッション復元データを消去", "Effacer l'instantané de récupération de session", "세션 복원 스냅샷 지우기", "Borrar la instantánea de recuperación de sesión", "Cancella lo snapshot di ripristino sessione" },
            ["menu_notify_toggle"] = new[] { "后台等待时弹通知", "Notify when a background tab is waiting", "背景等待時彈出通知", "Benachrichtigen, wenn ein Hintergrund-Tab wartet", "バックグラウンドのタブが待機中に通知", "Notifier quand un onglet en arrière-plan attend", "백그라운드 탭이 대기 중일 때 알림", "Notificar cuando una pestaña en segundo plano está esperando", "Notifica quando una scheda in background è in attesa" },
            ["menu_language"] = new[] { "语言 / Language", "Language / 语言", "語言 / Language", "Sprache / Language", "言語 / Language", "Langue / Language", "언어 / Language", "Idioma / Language", "Lingua / Language" },
            ["menu_theme"] = new[] { "主题", "Theme", "主題", "Design", "テーマ", "Thème", "테마", "Tema", "Tema" },
            ["theme_dark"] = new[] { "深色", "Dark", "深色", "Dunkel", "ダーク", "Sombre", "어두운 테마", "Oscuro", "Scuro" },
            ["theme_light"] = new[] { "浅色", "Light", "淺色", "Hell", "ライト", "Clair", "밝은 테마", "Claro", "Chiaro" },
            ["theme_system"] = new[] { "跟随系统", "Follow system", "跟隨系統", "System folgen", "システムに従う", "Suivre le système", "시스템 설정 따르기", "Seguir el sistema", "Segui il sistema" },
            ["menu_bypass_toggle"] = new[] { "跳过权限确认（危险）", "Skip permission prompts (risky)", "略過權限確認（危險）", "Berechtigungsabfragen überspringen (riskant)", "権限確認をスキップ（危険）", "Ignorer les confirmations d'autorisation (risqué)", "권한 확인 건너뛰기 (위험)", "Omitir confirmaciones de permiso (arriesgado)", "Salta le conferme di autorizzazione (rischioso)" },

            // ── Update ──
            ["update_latest"] = new[] { "当前已是最新版本 v{0}", "You're on the latest version (v{0})", "目前已是最新版本 v{0}", "Sie verwenden die neueste Version (v{0})", "最新バージョン (v{0}) を使用しています", "Vous utilisez la dernière version (v{0})", "최신 버전(v{0})을 사용 중입니다", "Estás en la última versión (v{0})", "Stai usando l'ultima versione (v{0})" },
            ["update_found_title"] = new[] { "发现新版本", "Update available", "發現新版本", "Update verfügbar", "更新があります", "Mise à jour disponible", "업데이트 사용 가능", "Actualización disponible", "Aggiornamento disponibile" },
            ["update_found_body"] = new[] { "新版本 v{0} 已发布，当前版本 v{1}。", "Version v{0} is available — you're on v{1}.", "新版本 v{0} 已發布，目前版本 v{1}。", "Version v{0} ist verfügbar – Sie verwenden v{1}.", "バージョン v{0} が利用可能です（現在 v{1}）。", "La version v{0} est disponible — vous utilisez v{1}.", "버전 v{0}을(를) 사용할 수 있습니다 — 현재 v{1}.", "La versión v{0} está disponible: tienes la v{1}.", "La versione v{0} è disponibile — stai usando la v{1}." },
            ["update_download_install"] = new[] { "下载并安装", "Download & install", "下載並安裝", "Herunterladen & installieren", "ダウンロードしてインストール", "Télécharger et installer", "다운로드 및 설치", "Descargar e instalar", "Scarica e installa" },
            ["update_goto_page"] = new[] { "前往下载页", "Open download page", "前往下載頁", "Download-Seite öffnen", "ダウンロード ページを開く", "Ouvrir la page de téléchargement", "다운로드 페이지 열기", "Abrir página de descargas", "Apri la pagina di download" },
            ["update_downloading_title"] = new[] { "下载更新", "Downloading update", "下載更新", "Update wird heruntergeladen", "更新をダウンロード中", "Téléchargement de la mise à jour", "업데이트 다운로드 중", "Descargando actualización", "Download dell'aggiornamento" },
            ["update_downloading"] = new[] { "正在下载 {0}...", "Downloading {0}…", "正在下載 {0}…", "{0} wird heruntergeladen…", "{0} をダウンロード中…", "Téléchargement de {0}…", "{0} 다운로드 중…", "Descargando {0}…", "Download di {0}…" },
            ["update_failed_title"] = new[] { "下载失败", "Download failed", "下載失敗", "Download fehlgeschlagen", "ダウンロード失敗", "Échec du téléchargement", "다운로드 실패", "Error de descarga", "Download non riuscito" },
            ["update_failed_body"] = new[] { "下载更新时出错，是否前往下载页面手动下载？", "Something went wrong downloading the update. Open the download page to get it manually?", "下載更新時發生錯誤，是否前往下載頁面手動下載？", "Beim Herunterladen des Updates ist ein Fehler aufgetreten. Download-Seite öffnen, um es manuell herunterzuladen?", "更新のダウンロード中に問題が発生しました。ダウンロード ページを開いて手動で取得しますか？", "Une erreur s'est produite lors du téléchargement de la mise à jour. Ouvrir la page de téléchargement pour l'obtenir manuellement ?", "업데이트를 다운로드하는 중 문제가 발생했습니다. 다운로드 페이지를 열어 수동으로 받으시겠습니까?", "Se produjo un error al descargar la actualización. ¿Abrir la página de descargas para obtenerla manualmente?", "Si è verificato un problema durante il download dell'aggiornamento. Aprire la pagina di download per scaricarlo manualmente?" },

            // ── Remote terminal ──
            ["remote_token_enable"] = new[] { "启用 Token 鉴权", "Require token authentication", "啟用 Token 驗證", "Token-Authentifizierung erforderlich", "トークン認証を有効化", "Exiger l'authentification par token", "토큰 인증 사용", "Requerir autenticación por token", "Richiedi autenticazione con token" },
            ["remote_autoincrement"] = new[] { "端口被占用时自动递增", "Auto-increment port if in use", "連接埠被佔用時自動遞增", "Port automatisch erhöhen, falls belegt", "ポート使用中の場合は自動的に番号を増やす", "Incrémenter le port automatiquement s'il est occupé", "포트가 사용 중이면 자동 증가", "Incrementar el puerto automáticamente si está en uso", "Incrementa automaticamente la porta se occupata" },
            ["remote_intro"] = new[] { "启动后，同一局域网内的设备可通过浏览器访问本机终端。", "Once started, devices on the same LAN can reach this machine's terminal from a browser.", "啟動後，同一區域網路內的裝置可透過瀏覽器存取本機終端機。", "Nach dem Start können Geräte im selben LAN das Terminal dieses Computers über einen Browser erreichen.", "起動後、同じ LAN 内のデバイスからブラウザーでこの PC のターミナルにアクセスできます。", "Une fois démarré, les appareils du même réseau local peuvent accéder au terminal de cette machine depuis un navigateur.", "시작하면 같은 LAN의 기기에서 브라우저로 이 컴퓨터의 터미널에 접속할 수 있습니다.", "Una vez iniciado, los dispositivos de la misma red local pueden acceder al terminal de este equipo desde un navegador.", "Una volta avviato, i dispositivi sulla stessa rete locale possono raggiungere il terminale di questo computer da un browser." },
            ["remote_listen_addr"] = new[] { "监听地址", "Listen address", "監聽位址", "Lauschadresse", "リッスン アドレス", "Adresse d'écoute", "수신 주소", "Dirección de escucha", "Indirizzo di ascolto" },
            ["remote_port"] = new[] { "端口", "Port", "連接埠", "Port", "ポート", "Port", "포트", "Puerto", "Porta" },
            ["remote_start_title"] = new[] { "启动远程终端", "Start remote terminal", "啟動遠端終端機", "Remote-Terminal starten", "リモート ターミナルを起動", "Démarrer le terminal distant", "원격 터미널 시작", "Iniciar terminal remoto", "Avvia terminale remoto" },
            ["remote_start"] = new[] { "启动", "Start", "啟動", "Starten", "起動", "Démarrer", "시작", "Iniciar", "Avvia" },
            ["remote_start_failed_title"] = new[] { "启动失败", "Failed to start", "啟動失敗", "Start fehlgeschlagen", "起動失敗", "Échec du démarrage", "시작 실패", "Error al iniciar", "Avvio non riuscito" },
            ["remote_start_failed_body"] = new[] { "无法启动远程终端服务：{0}", "Couldn't start the remote terminal service: {0}", "無法啟動遠端終端機服務：{0}", "Der Remote-Terminal-Dienst konnte nicht gestartet werden: {0}", "リモート ターミナル サービスを起動できませんでした：{0}", "Impossible de démarrer le service de terminal distant : {0}", "원격 터미널 서비스를 시작할 수 없습니다: {0}", "No se pudo iniciar el servicio de terminal remoto: {0}", "Impossibile avviare il servizio terminale remoto: {0}" },
            ["remote_started"] = new[] { "远程终端已启动！", "Remote terminal started!", "遠端終端機已啟動！", "Remote-Terminal gestartet!", "リモート ターミナルを起動しました！", "Terminal distant démarré !", "원격 터미널이 시작되었습니다!", "¡Terminal remoto iniciado!", "Terminale remoto avviato!" },
            ["remote_token_warn"] = new[] { "Token 已启用，请勿将链接泄露给他人。", "Token auth is on — don't share this link with anyone.", "Token 已啟用，請勿將連結洩漏給他人。", "Token-Authentifizierung ist aktiv – teilen Sie diesen Link mit niemandem.", "トークン認証が有効です。このリンクを他人に共有しないでください。", "L'authentification par token est activée — ne partagez ce lien avec personne.", "토큰 인증이 켜져 있습니다 — 이 링크를 누구와도 공유하지 마세요.", "La autenticación por token está activada: no compartas este enlace con nadie.", "L'autenticazione con token è attiva — non condividere questo link con nessuno." },
            ["remote_copy_link"] = new[] { "复制链接", "Copy link", "複製連結", "Link kopieren", "リンクをコピー", "Copier le lien", "링크 복사", "Copiar enlace", "Copia link" },
            ["remote_open_browser"] = new[] { "在浏览器中打开", "Open in browser", "在瀏覽器中開啟", "Im Browser öffnen", "ブラウザーで開く", "Ouvrir dans le navigateur", "브라우저에서 열기", "Abrir en el navegador", "Apri nel browser" },
            ["remote_running_status"] = new[] { "远程终端正在运行  |  连接客户端: {0}", "Remote terminal running  |  Clients connected: {0}", "遠端終端機執行中  |  連線用戶端: {0}", "Remote-Terminal läuft  |  Verbundene Clients: {0}", "リモート ターミナル実行中  |  接続中のクライアント: {0}", "Terminal distant en cours  |  Clients connectés : {0}", "원격 터미널 실행 중  |  연결된 클라이언트: {0}", "Terminal remoto en ejecución  |  Clientes conectados: {0}", "Terminale remoto in esecuzione  |  Client connessi: {0}" },
            ["remote_stop"] = new[] { "停止服务", "Stop service", "停止服務", "Dienst stoppen", "サービスを停止", "Arrêter le service", "서비스 중지", "Detener servicio", "Arresta servizio" },

            // ── Tab context menu ──
            ["tab_split_right"] = new[] { "向右分屏", "Split right", "向右分割", "Rechts teilen", "右に分割", "Diviser à droite", "오른쪽으로 분할", "Dividir a la derecha", "Dividi a destra" },
            ["tab_split_down"] = new[] { "向下分屏", "Split down", "向下分割", "Nach unten teilen", "下に分割", "Diviser vers le bas", "아래로 분할", "Dividir hacia abajo", "Dividi in basso" },
            ["tab_close"] = new[] { "关闭标签页", "Close tab", "關閉分頁", "Tab schließen", "タブを閉じる", "Fermer l'onglet", "탭 닫기", "Cerrar pestaña", "Chiudi scheda" },
            ["tab_close_others"] = new[] { "关闭其他标签页", "Close other tabs", "關閉其他分頁", "Andere Tabs schließen", "他のタブを閉じる", "Fermer les autres onglets", "다른 탭 닫기", "Cerrar las otras pestañas", "Chiudi le altre schede" },
            ["tab_close_left"] = new[] { "关闭左侧标签页", "Close tabs to the left", "關閉左側分頁", "Tabs links schließen", "左側のタブを閉じる", "Fermer les onglets à gauche", "왼쪽 탭 닫기", "Cerrar las pestañas de la izquierda", "Chiudi le schede a sinistra" },
            ["tab_close_right"] = new[] { "关闭右侧标签页", "Close tabs to the right", "關閉右側分頁", "Tabs rechts schließen", "右側のタブを閉じる", "Fermer les onglets à droite", "오른쪽 탭 닫기", "Cerrar las pestañas de la derecha", "Chiudi le schede a destra" },

            // ── Project menu ──
            ["proj_default"] = new[] { "默认: {0}", "Default: {0}", "預設: {0}", "Standard: {0}", "既定: {0}", "Par défaut : {0}", "기본값: {0}", "Predeterminado: {0}", "Predefinito: {0}" },
            ["proj_new_claude"] = new[] { "新建 Claude 标签", "New Claude tab", "新增 Claude 分頁", "Neuer Claude-Tab", "新しい Claude タブ", "Nouvel onglet Claude", "새 Claude 탭", "Nueva pestaña de Claude", "Nuova scheda Claude" },
            ["proj_new_codex"] = new[] { "新建 Codex 标签", "New Codex tab", "新增 Codex 分頁", "Neuer Codex-Tab", "新しい Codex タブ", "Nouvel onglet Codex", "새 Codex 탭", "Nueva pestaña de Codex", "Nuova scheda Codex" },
            ["proj_open_claude"] = new[] { "用 Claude 打开", "Open with Claude", "用 Claude 開啟", "Mit Claude öffnen", "Claude で開く", "Ouvrir avec Claude", "Claude로 열기", "Abrir con Claude", "Apri con Claude" },
            ["proj_open_codex"] = new[] { "用 Codex 打开", "Open with Codex", "用 Codex 開啟", "Mit Codex öffnen", "Codex で開く", "Ouvrir avec Codex", "Codex로 열기", "Abrir con Codex", "Apri con Codex" },
            ["proj_open_dir"] = new[] { "打开项目目录", "Open project folder", "開啟專案目錄", "Projektordner öffnen", "プロジェクト フォルダーを開く", "Ouvrir le dossier du projet", "프로젝트 폴더 열기", "Abrir la carpeta del proyecto", "Apri la cartella del progetto" },
            ["proj_remove"] = new[] { "移除 {0}", "Remove {0}", "移除 {0}", "{0} entfernen", "{0} を削除", "Supprimer {0}", "{0} 제거", "Quitar {0}", "Rimuovi {0}" },
            ["proj_add"] = new[] { "添加项目...", "Add project…", "新增專案…", "Projekt hinzufügen…", "プロジェクトを追加…", "Ajouter un projet…", "프로젝트 추가…", "Añadir proyecto…", "Aggiungi progetto…" },

            // ── Toast ──
            ["toast_title"] = new[] { "CC Pad — 需要你的输入", "CC Pad — needs your input", "CC Pad — 需要你的輸入", "CC Pad – benötigt Ihre Eingabe", "CC Pad — 入力が必要です", "CC Pad — votre saisie est requise", "CC Pad — 입력이 필요합니다", "CC Pad: necesita tu entrada", "CC Pad — richiede il tuo input" },
            ["toast_one"] = new[] { "一个会话正在等待你的回答", "A session is waiting for your answer", "有一個工作階段正在等待你的回覆", "Eine Sitzung wartet auf Ihre Antwort", "あるセッションが応答を待っています", "Une session attend votre réponse", "한 세션이 응답을 기다리고 있습니다", "Una sesión está esperando tu respuesta", "Una sessione è in attesa della tua risposta" },
            ["toast_named"] = new[] { "「{0}」正在等待你的回答", "\"{0}\" is waiting for your answer", "「{0}」正在等待你的回覆", "„{0}“ wartet auf Ihre Antwort", "「{0}」が応答を待っています", "« {0} » attend votre réponse", "「{0}」이(가) 응답을 기다리고 있습니다", "«{0}» está esperando tu respuesta", "«{0}» è in attesa della tua risposta" },

            // ── Tooltips / button labels ──
            ["tip_check_update"] = new[] { "检查更新", "Check for updates", "檢查更新", "Nach Updates suchen", "更新を確認", "Rechercher des mises à jour", "업데이트 확인", "Buscar actualizaciones", "Controlla aggiornamenti" },
            ["tip_about"] = new[] { "关于 / 设置", "About / Settings", "關於 / 設定", "Info / Einstellungen", "情報 / 設定", "À propos / Paramètres", "정보 / 설정", "Acerca de / Configuración", "Informazioni / Impostazioni" },
            ["tip_workspace"] = new[] { "工作区", "Workspace", "工作區", "Arbeitsbereich", "ワークスペース", "Espace de travail", "작업 영역", "Espacio de trabajo", "Area di lavoro" },
            ["btn_workspace"] = new[] { "工作区", "Workspace", "工作區", "Arbeitsbereich", "ワークスペース", "Espace de travail", "작업 영역", "Espacio de trabajo", "Area di lavoro" },
            ["tip_project"] = new[] { "从项目启动新标签页", "Launch a new tab from a project", "從專案啟動新分頁", "Neuen Tab aus einem Projekt öffnen", "プロジェクトから新しいタブを起動", "Lancer un nouvel onglet à partir d'un projet", "프로젝트에서 새 탭 실행", "Abrir una nueva pestaña desde un proyecto", "Avvia una nuova scheda da un progetto" },
            ["btn_project"] = new[] { "项目", "Projects", "專案", "Projekte", "プロジェクト", "Projets", "프로젝트", "Proyectos", "Progetti" },
            ["tip_resize_tabs"] = new[] { "拖拽调整页签高度 · 双击恢复默认", "Drag to resize the tab strip · double-click to reset", "拖曳調整分頁高度 · 按兩下還原預設", "Ziehen, um die Tableiste zu verkleinern · Doppelklick zum Zurücksetzen", "ドラッグでタブの高さを調整 · ダブルクリックで既定に戻す", "Glisser pour redimensionner la barre d'onglets · double-clic pour réinitialiser", "드래그하여 탭 높이 조정 · 더블클릭하여 기본값으로 재설정", "Arrastra para ajustar la altura de las pestañas · doble clic para restablecer", "Trascina per ridimensionare la barra delle schede · doppio clic per ripristinare" },

            // ── Windows shell (registry) ──
            ["shell_open"] = new[] { "在 CC Pad 中打开", "Open in CC Pad", "在 CC Pad 中開啟", "In CC Pad öffnen", "CC Pad で開く", "Ouvrir dans CC Pad", "CC Pad에서 열기", "Abrir en CC Pad", "Apri in CC Pad" },
            ["shell_workspace_type"] = new[] { "CCPad 工作区", "CCPad workspace", "CCPad 工作區", "CCPad-Arbeitsbereich", "CCPad ワークスペース", "Espace de travail CCPad", "CCPad 작업 영역", "Espacio de trabajo CCPad", "Area di lavoro CCPad" },

            // ── Remote web page (served to browsers) ──
            ["web_collapse"] = new[] { "折叠", "Collapse", "摺疊", "Einklappen", "折りたたむ", "Réduire", "접기", "Contraer", "Comprimi" },
            ["web_expand"] = new[] { "展开侧边栏", "Expand sidebar", "展開側邊欄", "Seitenleiste erweitern", "サイドバーを展開", "Développer la barre latérale", "사이드바 펼치기", "Expandir barra lateral", "Espandi barra laterale" },
            ["web_no_sessions"] = new[] { "没有活跃的终端会话", "No active terminal sessions", "沒有作用中的終端機工作階段", "Keine aktiven Terminalsitzungen", "アクティブなターミナル セッションがありません", "Aucune session de terminal active", "활성 터미널 세션 없음", "No hay sesiones de terminal activas", "Nessuna sessione di terminale attiva" },

            // ── Misc ──
            ["autoconfirm_title"] = new[] { "Auto Confirm (自动回车)", "Auto Confirm", "Auto Confirm (自動回車)", "Auto-Bestätigung", "自動確認", "Confirmation auto", "자동 확인", "Confirmación automática", "Conferma automatica" },
            ["port_in_use"] = new[] { "端口 {0} 已被占用", "Port {0} is already in use", "連接埠 {0} 已被佔用", "Port {0} ist bereits belegt", "ポート {0} は既に使用されています", "Le port {0} est déjà utilisé", "포트 {0}이(가) 이미 사용 중입니다", "El puerto {0} ya está en uso", "La porta {0} è già in uso" },
            ["port_retry_failed"] = new[] { "，尝试了 {0} 个端口均失败", " — tried {0} ports, all failed", "，已嘗試 {0} 個連接埠均失敗", " – {0} Ports versucht, alle fehlgeschlagen", "（{0} 個のポートを試行しましたが、すべて失敗しました）", " — {0} ports essayés, tous en échec", " — {0}개 포트를 시도했지만 모두 실패", " — se probaron {0} puertos, todos fallaron", " — provate {0} porte, tutte fallite" },
        };
    }
}

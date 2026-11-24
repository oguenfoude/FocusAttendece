using FocusPlus.Application.Services;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application;

/// <summary>Bilingual labels shared by the UI, the CSV export and the printable report.</summary>
public static class Labels
{
    public static string Day(DayOfWeek day, bool arabic)
    {
        var ar = new[] { "الأحد", "الإثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" };
        var fr = new[] { "Dimanche", "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi", "Samedi" };
        return arabic ? ar[(int)day] : fr[(int)day];
    }

    public static string ShortDay(DayOfWeek day, bool arabic)
    {
        var ar = new[] { "أحد", "إثنين", "ثلاثاء", "أربعاء", "خميس", "جمعة", "سبت" };
        var fr = new[] { "Dim", "Lun", "Mar", "Mer", "Jeu", "Ven", "Sam" };
        return arabic ? ar[(int)day] : fr[(int)day];
    }

    public static string Status(AttendanceStatus status, bool arabic) => status switch
    {
        AttendanceStatus.NoData => arabic ? "لا بيانات" : "Aucune donnée",
        AttendanceStatus.RestDay => arabic ? "راحة أسبوعية" : "Repos",
        AttendanceStatus.Present => arabic ? "حاضر" : "Présent",
        AttendanceStatus.Late => arabic ? "متأخر" : "En retard",
        AttendanceStatus.EarlyLeave => arabic ? "خروج مبكر" : "Sortie anticipée",
        AttendanceStatus.LateAndEarlyLeave => arabic ? "تأخر وخروج مبكر" : "Retard + sortie anticipée",
        AttendanceStatus.Absent => arabic ? "غائب" : "Absent",
        AttendanceStatus.Leave => arabic ? "عطلة" : "Congé",
        AttendanceStatus.Mission => arabic ? "مهمة" : "Mission",
        AttendanceStatus.MissingOutScan => arabic ? "بصمة خروج ناقصة" : "Sortie non pointée",
        AttendanceStatus.ExtraDay => arabic ? "يوم إضافي" : "Jour supplémentaire",
        _ => status.ToString()
    };

    public static string Kind(AdjustmentKind kind, bool arabic) => kind switch
    {
        AdjustmentKind.ManualPunch => arabic ? "بصمة يدوية" : "Pointage manuel",
        AdjustmentKind.Mission => arabic ? "أمر بمهمة" : "Ordre de mission",
        AdjustmentKind.ExitPermit => arabic ? "رخصة خروج" : "Bon de sortie",
        AdjustmentKind.AnnualLeave => arabic ? "عطلة سنوية" : "Congé annuel",
        AdjustmentKind.SickLeave => arabic ? "عطلة مرضية" : "Congé maladie",
        AdjustmentKind.OtherLeave => arabic ? "عطلة أخرى" : "Autre congé",
        _ => kind.ToString()
    };

    public static string Note(AttendanceNote note, bool arabic) => note switch
    {
        AttendanceNote.MissingOutScan => arabic ? "بصمة خروج ناقصة" : "Sortie non pointée",
        AttendanceNote.MissingInScan => arabic ? "بصمة دخول ناقصة" : "Entrée non pointée",
        AttendanceNote.LateArrival => arabic ? "تأخر في الحضور" : "Arrivée tardive",
        AttendanceNote.EarlyLeave => arabic ? "خروج قبل الوقت" : "Départ anticipé",
        AttendanceNote.LunchDeducted => arabic ? "خصم الاستراحة تلقائياً" : "Pause déduite automatiquement",
        AttendanceNote.OffDayWorked => arabic ? "عمل في يوم غير مبرمج" : "Travail hors jour programmé",
        AttendanceNote.ManualPunchUsed => arabic ? "استُعملت بصمة يدوية معتمدة" : "Pointage manuel vérifié utilisé",
        AttendanceNote.SingleScan => arabic ? "بصمة واحدة فقط" : "Battement unique",
        AttendanceNote.MissionCredited => arabic ? "ساعات مهمة معتمدة" : "Heures de mission créditées",
        AttendanceNote.ExitPermitCredited => arabic ? "ساعات خروج معتمدة" : "Heures de sortie créditées",
        AttendanceNote.LeaveCredited => arabic ? "عطلة معتمدة" : "Congé crédité",
        AttendanceNote.AntiBounceFiltered => arabic ? "تم تجاهل بصمة مزدوجة" : "Double pointage ignoré",
        AttendanceNote.TargetReached => arabic ? "الهدف اليومي محقق" : "Objectif journalier atteint",
        AttendanceNote.BelowTarget => arabic ? "أقل من الهدف اليومي" : "Sous l'objectif journalier",
        _ => note.ToString()
    };

    public static string ShiftMode(WorkMode mode, bool arabic) =>
        mode == WorkMode.FreeTime
            ? (arabic ? "وقت حر" : "Temps libre")
            : (arabic ? "دوام ثابت" : "Horaire fixe");

    public static string WeekDays(WeekDays mask, bool arabic) =>
        string.Join(" ", new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday }
            .Where(d => mask.Includes(d))
            .Select(d => ShortDay(d, arabic)));

    public static string Minutes(int minutes) =>
        $"{minutes / 60:00}:{minutes % 60:00}";

    public static string Money(decimal value) => $"{value:N2}";

    public static string NetworkStage(NetworkUpdateStage stage, bool arabic) => stage switch
    {
        NetworkUpdateStage.Validating => arabic ? "التحقق من المعطيات" : "Validation des paramètres",
        NetworkUpdateStage.Writing => arabic ? "كتابة العنوان في الجهاز" : "Écriture dans le terminal",
        NetworkUpdateStage.Rebooting => arabic ? "إعادة تشغيل الجهاز" : "Redémarrage du terminal",
        NetworkUpdateStage.WaitingForDevice => arabic ? "انتظار عودة الجهاز" : "Attente du terminal",
        NetworkUpdateStage.Reconnected => arabic ? "تم الاتصال بالعنوان الجديد" : "Reconnecté à la nouvelle adresse",
        NetworkUpdateStage.SyncingClock => arabic ? "مزامنة الساعة" : "Synchronisation de l'horloge",
        NetworkUpdateStage.PullingPunches => arabic ? "سحب البصمات" : "Récupération des pointages",
        NetworkUpdateStage.Completed => arabic ? "اكتملت العملية" : "Terminé",
        NetworkUpdateStage.Failed => arabic ? "فشلت العملية" : "Échec",
        _ => stage.ToString()
    };

    /// <summary>User facing message for every service error code.</summary>
    public static string Error(string? code, bool arabic, string? detail = null)
    {
        var prefix = arabic ? "تعذر إتمام العملية" : "Opération impossible";
        var message = code switch
        {
            ErrorCodes.Required => arabic ? "يرجى تعبئة كل الحقول الإلزامية." : "Veuillez remplir les champs obligatoires.",
            ErrorCodes.DuplicateCode => arabic ? "هذا الرمز مستعمل مسبقاً." : "Ce code est déjà utilisé.",
            ErrorCodes.DuplicateMatricule => arabic ? "رقم التسجيل الوظيفي (الماتريكول) مستعمل مسبقاً." : "Ce matricule existe déjà.",
            ErrorCodes.DuplicatePin => arabic ? "المعرف البيومتري مستعمل من طرف موظف آخر." : "Ce PIN biométrique est déjà attribué.",
            ErrorCodes.DuplicateSerial => arabic ? "هذا الجهاز مسجل مسبقاً بنفس الرقم التسلسلي." : "Ce terminal est déjà enregistré avec le même numéro de série.",
            ErrorCodes.DuplicateEndpoint => arabic ? "عنوان IP والمنفذ مستعملان من طرف جهاز آخر." : "Cette adresse IP et ce port sont déjà utilisés.",
            ErrorCodes.NotFound => arabic ? "العنصر المطلوب غير موجود." : "L'élément demandé est introuvable.",
            ErrorCodes.InvalidIp => arabic ? "عنوان IP غير صالح." : "Adresse IP invalide.",
            ErrorCodes.InvalidPort => arabic ? "المنفذ غير صالح (1 إلى 65535)." : "Port invalide (1 à 65535).",
            ErrorCodes.InvalidTime => arabic ? "التوقيت غير صالح: يجب أن يكون الدوام الصباحي قبل الغداء وقبل نهاية اليوم." : "Horaire invalide : l'ordre des plages doit être respecté.",
            ErrorCodes.DeviceUnreachable => arabic ? "تعذر الاتصال بالجهاز على المنفذ 4370." : "Le terminal est injoignable sur le port 4370.",
            ErrorCodes.DeviceSerialMismatch => arabic ? "الرقم التسلسلي لا يطابق الجهاز المسجل." : "Le numéro de série ne correspond pas au terminal enregistré.",
            ErrorCodes.Hardware => arabic ? "فشل تنفيذ العملية على الجهاز." : "L'opération matérielle a échoué.",
            ErrorCodes.InvalidCredentials => arabic ? "اسم المستخدم أو كلمة المرور غير صحيحة." : "Nom d'utilisateur ou mot de passe incorrect.",
            ErrorCodes.AccountArchived => arabic ? "هذا الحساب مؤرشف." : "Ce compte est archivé.",
            ErrorCodes.PasswordTooShort => arabic ? "كلمة المرور يجب أن تحتوي على 8 أحرف على الأقل." : "Le mot de passe doit contenir au moins 8 caractères.",
            ErrorCodes.LastDefaultRule => arabic ? "لا يمكن أرشفة آخر وردية متبقية." : "Impossible d'archiver la dernière règle de travail.",
            ErrorCodes.InUse => arabic ? "لا يمكن الحذف: العنصر مستعمل من طرف موظفين أو مصالح." : "Suppression impossible : élément utilisé par des employés ou services.",
            ErrorCodes.InvalidRange => arabic ? "المدى الزمني غير صالح." : "Plage de dates invalide.",
            _ => arabic ? "حدث خطأ غير متوقع." : "Une erreur inattendue est survenue."
        };

        if (!string.IsNullOrWhiteSpace(detail) && code is not (ErrorCodes.Required or ErrorCodes.DeviceUnreachable or ErrorCodes.InvalidCredentials))
        {
            return $"{prefix} — {message} ({detail})";
        }

        return string.IsNullOrWhiteSpace(detail) ? message : $"{message}\n{detail}";
    }
}

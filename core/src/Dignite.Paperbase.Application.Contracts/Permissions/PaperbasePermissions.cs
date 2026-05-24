using Volo.Abp.Reflection;

namespace Dignite.Paperbase.Permissions;

public class PaperbasePermissions
{
    public const string GroupName = "Paperbase";

    public static class Documents
    {
        public const string Default = GroupName + ".Documents";
        public const string Upload = Default + ".Upload";
        public const string Delete = Default + ".Delete";
        public const string PermanentDelete = Default + ".PermanentDelete";
        public const string Restore = Default + ".Restore";
        public const string Export = Default + ".Export";
        public const string ConfirmClassification = Default + ".ConfirmClassification";

        public static class Pipelines
        {
            public const string Default = Documents.Default + ".Pipelines";
            public const string Retry = Default + ".Retry";
        }

        public static class Templates
        {
            public const string Default = Documents.Default + ".Templates";
            public const string Create = Default + ".Create";
            public const string Update = Default + ".Update";
            public const string Delete = Default + ".Delete";
        }
    }

    public static string[] GetAll()
    {
        return ReflectionHelper.GetPublicConstantsRecursively(typeof(PaperbasePermissions));
    }
}

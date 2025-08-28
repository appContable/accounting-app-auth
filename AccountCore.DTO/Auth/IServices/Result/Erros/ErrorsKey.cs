using System.ComponentModel;

namespace AccountCore.Services.Auth.Errors
{
    public enum ErrorsKey
    {
        [Description("ExistsWithName")]
        ExistsWithName = 0,

        [Description("IncorrectEmailOrPassword")]
        IncorrectEmailOrPassword = 1,

        [Description("NotExistsFilter")]
        NotExistsFilter = 2,

        [Description("NoCategories")]
        NoCategories = 3,

        [Description("NoQuestions")]
        NoQuestions = 4,

        [Description("EmailExists")]
        EmailExists = 5,

        [Description("InvalidRol")]
        InvalidRol = 6,

        [Description("NeedOneUser")]
        NeedOneUser = 7,

        [Description("NeedOneRol")]
        NeedOneRol = 8,

        [Description("FilterAlreadyPublished")]
        FilterAlreadyPublished = 9,

        [Description("UserNotExist")]
        UserNotExist = 10,

        [Description("InvitationExpired")]
        InvitationExpired = 11,

        [Description("InvalidInvitation")]
        InvalidInvitation = 12,

        [Description("HashExpired")]
        HashExpired = 13,

        [Description("NoAnswers")]
        NoAnswers = 14,

        [Description("FilterAlreadyResponded")]
        FilterAlreadyResponded = 15,

        [Description("NotExistsQuestion")]
        NotExistsQuestion = 16,

        [Description("Argument")]
        Argument = 17,

        [Description("NotLoggedUser")]
        NotLoggedUser = 18,

        [Description("NotUnique")]
        NotUnique = 19,

        [Description("Lock")]
        Lock = 20,

        [Description("ResetPass")]
        ResetPass = 21,

        [Description("InvalidExchangeValue")]
        InvalidExchangeValue = 22,

        [Description("WeakPassword")]
        WeakPassword = 501,

        [Description("Internal Error Server")]
        InternalErrorCode = 500,

        [Description("Forbbinden")]
        Forbbinden = 403,
    }
}

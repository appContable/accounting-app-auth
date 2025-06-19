using AuthServices.Errors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AuthDTO.IServices.Result
{
    public class ServiceResult<T>

    {
        public T? Value { get; set; }

        public bool Success { get; set; }

        public List<KeyValuePair<string, string>>? Errors { get; set; }

        public static ServiceResult<T> Ok(T value)
        {
            return new ServiceResult<T> { Value = value, Success = true };
        }

        public static ServiceResult<T> Error(List<KeyValuePair<string, string>> errors)
        {
            return new ServiceResult<T> { Errors = errors, Success = false };
        }

        public static ServiceResult<T> Error(string code, string? message = null)
        {
            return Error(new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(code, message ?? string.Empty) });
        }

        public static ServiceResult<T> Error(ErrorsKey code, string? message = null)
        {
            return Error(code + string.Empty, message);
        }

    }
}

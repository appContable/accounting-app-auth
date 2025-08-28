using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AccountCore.DTO.Auth.Parameters
{
    public class UserParameterDTO
    {
        public string? Name { get; set; }

        public string? Email { get; set; }


        /// <summary>
        /// load all the params
        /// </summary>
        /// <returns></returns>
        public static List<UserParameterDTO> LoadParameters(string? search)
        {
            var response = new  List<UserParameterDTO>();

            if (string.IsNullOrEmpty(search))
            {
                return response;
            }

            var words = search.Split(" ");

            foreach (var word in words)
            {
                if (string.IsNullOrEmpty(word))
                {
                    continue;
                }
                var term = new UserParameterDTO();

                if (word.Contains("@"))
                {
                    term.Email = word;
                }
                else
                {
                    term.Name= word;
                }

                response.Add(term);
            }

            return response;
        }

    }
}

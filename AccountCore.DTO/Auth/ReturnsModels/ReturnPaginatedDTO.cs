namespace AccountCore.DTO.Auth.ReturnsModels
{
    public class ReturnPaginatedDTO <T>
    {
        public int Page { get; set; }
        public int Items { get; set; }
        public int TotalItems { get; set; }
        public IEnumerable<T> Data { get; set; }
    }
}

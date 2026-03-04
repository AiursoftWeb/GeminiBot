using System;
using System.Net.Http;
class Program {
    static void Main() {
        var ex = new HttpRequestException("test", null, System.Net.HttpStatusCode.UnprocessableEntity);
        Console.WriteLine(ex.StatusCode);
    }
}

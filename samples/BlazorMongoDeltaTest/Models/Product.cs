using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BlazorMongoDeltaTest.Models;

public class Product
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Name     { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price   { get; set; }
}

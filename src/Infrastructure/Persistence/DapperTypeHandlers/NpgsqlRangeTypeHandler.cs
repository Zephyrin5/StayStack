using Dapper;
using NpgsqlTypes;
using System.Data;
namespace Persistence.DapperTypeHandlers;

public class NpgsqlRangeTypeHandler<T> : SqlMapper.TypeHandler<NpgsqlRange<T>>
{
    public override void SetValue(IDbDataParameter parameter, NpgsqlRange<T> value)
    {
        parameter.Value = value;
    }

    public override NpgsqlRange<T> Parse(object value)
    {
        return value is NpgsqlRange<T> range ? range : default;
    }
}

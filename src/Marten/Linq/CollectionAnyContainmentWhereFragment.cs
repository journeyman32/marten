using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Baseline;
using Marten.Schema;
using Marten.Util;
using NpgsqlTypes;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ResultOperators;

namespace Marten.Linq
{
    // TODO -- this is going to have to get redone
    public class CollectionAnyContainmentWhereFragment: IWhereFragment
    {
        private static readonly Type[] supportedTypes = new Type[] { typeof(string), typeof(Guid) };
        private readonly MemberInfo[] _members;
        private readonly ISerializer _serializer;
        private readonly SubQueryExpression _expression;
        private readonly IQueryableDocument _mapping;

        [Obsolete("Use the constructor that takes IQueryableDocument instead. This might be removed in v4.0.")]
        public CollectionAnyContainmentWhereFragment(MemberInfo[] members, ISerializer serializer, SubQueryExpression expression) : this(members, serializer, expression, null)
        {
        }

        public CollectionAnyContainmentWhereFragment(MemberInfo[] members, ISerializer serializer, SubQueryExpression expression, IQueryableDocument mapping)
        {
            _members = members;
            _serializer = serializer;
            _expression = expression;
            _mapping = mapping;
        }

        public void Apply(CommandBuilder builder)
        {
            var wheres = _expression
                .QueryModel
                .BodyClauses
                .OfType<WhereClause>()
                .Select(x => x.Predicate)
                .ToArray();

            if (!wheres.All(x => x is BinaryExpression || x is SubQueryExpression))
            {
                throw new NotSupportedException();
            }

            var binaryExpressions = wheres.OfType<BinaryExpression>().ToArray();
            var subQueryExpressions = wheres.OfType<SubQueryExpression>().ToArray();

            var conditions = new List<string>();
            conditions.AddRange(buildBinary(binaryExpressions, builder));
            conditions.AddRange(subQueryExpressions.Select(s => buildSubQuery(s, builder)));

            if (conditions.Any())
            {
                builder.Append(conditions[0]);
                for (int i = 1; i < conditions.Count; i++)
                {
                    builder.Append(" AND ");
                    builder.Append(conditions[i]);
                }
            }
        }

        private IEnumerable<string> buildBinary(BinaryExpression[] binaryExpressions, CommandBuilder command)
        {
            if (!binaryExpressions.Any())
            {
                yield break;
            }

            var dictionary = new Dictionary<string, object>();

            // Are we querying directly againt the elements as you would for primitive types?
            if (binaryExpressions.All(x => x.Left is QuerySourceReferenceExpression && x.Right is ConstantExpression))
            {
                if (binaryExpressions.Any(x => x.NodeType != ExpressionType.Equal))
                {
                    throw new NotSupportedException("Only the equality operator is supported on Collection.Any(x => x) searches directly against the element");
                }

                var values = binaryExpressions.Select(x => x.Right.Value()).ToArray();
                if (_members.Length == 1)
                {
                    dictionary.Add(_members.Single().Name, values);
                }
                else
                {
                    throw new NotSupportedException();
                }
            }
            else
            {
                var search = new Dictionary<string, object>();
                binaryExpressions.Each(x => gatherSearch(x, search, _serializer));

                if (_members.Length == 1)
                {
                    dictionary.Add(_members.Single().Name, new[] { search });
                }
                else
                {
                    var current = dictionary;

                    for (int i = 0; i < _members.Length - 1; i++)
                    {
                        var dict = new Dictionary<string, object>();
                        current.Add(_members[i].Name, dict);

                        current = dict;
                    }

                    current.Add(_members.Last().Name, new[] { search });
                }
            }

            var json = _serializer.ToCleanJson(dictionary);
            var param = command.AddParameter(json);
            param.NpgsqlDbType = NpgsqlDbType.Jsonb;

            yield return $"d.data @> :{param.ParameterName}";
        }

        public bool Contains(string sqlText)
        {
            return false;
        }

        private static void gatherSearch(BinaryExpression x, Dictionary<string, object> search, ISerializer serializer)
        {
            if (x.NodeType == ExpressionType.AndAlso)
            {
                if (x.Left is BinaryExpression)
                    gatherSearch(x.Left.As<BinaryExpression>(), search, serializer);
                if (x.Right is BinaryExpression)
                    gatherSearch(x.Right.As<BinaryExpression>(), search, serializer);
            }
            else if (x.NodeType == ExpressionType.Equal)
            {
                ContainmentWhereFragment.CreateDictionaryForSearch(x, search, serializer);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private string buildSubQuery(SubQueryExpression subQuery, CommandBuilder command)
        {
            var contains = subQuery.QueryModel.ResultOperators.OfType<ContainsResultOperator>().FirstOrDefault();
            if (contains == null)
            {
                throw new NotSupportedException("Only the Contains() operator is supported on subqueries within Collection.Any() searches");
            }

            // build rhs of ?|
            var from = subQuery.QueryModel.MainFromClause.FromExpression as ConstantExpression;
            if (from == null || !supportedTypes.Any(supp => isListOrArrayOf(from.Type, supp)))
            {
                throwNotSupportedContains();
            }

            //TODO: this won't work for enumeration types. Only works with strings, so we have
            // to exactly map the ToString() like the underlying serializer would. Blech.
            var values = new List<string>();

            var enumerable = ((System.Collections.IEnumerable)from.Value);

            foreach (var obj in enumerable)
            {
                values.Add(obj.ToString());
            }

            var fromParam = command.AddParameter(values.ToArray());
            fromParam.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;

            // check/build lhs of ?|
            var item = contains.Item as QuerySourceReferenceExpression;
            if (item == null)
            {
                throwNotSupportedContains();
            }
            if (!supportedTypes.Any(supp => supp == item.ReferencedQuerySource.ItemType))
            {
                throwNotSupportedContains();
            }
            var itemSource = item.ReferencedQuerySource as MainFromClause;
            if (itemSource == null)
            {
                throwNotSupportedContains();
            }
            var member = itemSource.FromExpression as MemberExpression;
            if (member == null)
            {
                throwNotSupportedContains();
            }
            var visitor = new FindMembers();
            visitor.Visit(member);
            var members = visitor.Members;
            if (!members.Any())
                throwNotSupportedContains();
            var path = _mapping?.FieldFor(members).SqlLocator ?? $"CAST ({CommandBuilder.BuildJsonStringLocator("d.data", members.ToArray(), _serializer.Casing)} as jsonb)";
            return $"{path} ?| :{fromParam.ParameterName}";
        }

        private void throwNotSupportedContains()
        {
            throw new NotSupportedException($"The Contains() operator on subqueries within Collection.Any() searches only supports constant array/lists of {string.Join(" or ", supportedTypes.Select(t => t.Name))} expressions");
        }

        private bool isListOrArrayOf(Type value, Type valid)
        {
            if (value.IsArray && valid.IsAssignableFrom(value.GetElementType()))
                return true;
            if (value.IsGenericEnumerable())
            {
                var typeDef = value.GetGenericTypeDefinition();
                if (typeDef.IsAssignableFrom(typeof(List<>)) && valid.IsAssignableFrom(typeDef.GenericTypeArguments[0]))
                    return true;
            }
            return false;
        }
    }
}

using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Baseline.Reflection;
using Marten.Schema;
using Marten.Testing.Documents;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Marten.Testing.Schema
{
    public class DuplicatedFieldTests
    {
        private DuplicatedField theField = new DuplicatedField(EnumStorage.AsInteger, new MemberInfo[] { ReflectionHelper.GetProperty<User>(x => x.FirstName) });

        [Fact]
        public void default_role_is_search()
        {
            theField
                .Role.ShouldBe(DuplicatedFieldRole.Search);
        }

        [Fact]
        public void create_table_column_for_non_indexed_search()
        {
            var column = theField.ToColumn();
            column.Name.ShouldBe("first_name");
            column.Type.ShouldBe("varchar");
        }

        [Fact]
        public void upsert_argument_defaults()
        {
            theField.UpsertArgument.Arg.ShouldBe("arg_first_name");
            theField.UpsertArgument.Column.ShouldBe("first_name");
            theField.UpsertArgument.PostgresType.ShouldBe("varchar");
        }

        [Fact]
        public void sql_locator_with_default_column_name()
        {
            theField.SqlLocator.ShouldBe("d.first_name");
        }

        [Fact]
        public void sql_locator_with_custom_column_name()
        {
            theField.ColumnName = "x_first_name";
            theField.SqlLocator.ShouldBe("d.x_first_name");
        }

        [Fact]
        public void enum_field()
        {
            var field = DuplicatedField.For<Target>(EnumStorage.AsString, x => x.Color);
            field.UpsertArgument.DbType.ShouldBe(NpgsqlDbType.Varchar);
            field.UpsertArgument.PostgresType.ShouldBe("varchar");

            var constant = Expression.Constant((int)Colors.Blue);

            field.GetValue(constant).ShouldBe(Colors.Blue.ToString());
        }

        [Theory]
        [InlineData(EnumStorage.AsInteger, "color = CAST(data ->> 'Color' as integer)")]
        [InlineData(EnumStorage.AsString, "color = data ->> 'Color'")]
        public void storage_is_set_when_passed_in(EnumStorage storageMode, string expectedUpdateFragment)
        {
            var field = DuplicatedField.For<Target>(storageMode, x => x.Color);
            field.UpdateSqlFragment().ShouldBe(expectedUpdateFragment);
        }

        [Theory]
        [InlineData(null, "string = data ->> 'String'")]
        [InlineData("varchar", "string = data ->> 'String'")]
        [InlineData("text", "string = data ->> 'String'")]
        public void pg_type_is_used_for_string(string pgType, string expectedUpdateFragment)
        {
            var field = DuplicatedField.For<Target>(EnumStorage.AsInteger, x => x.String);
            field.PgType = pgType ?? field.PgType;

            field.UpdateSqlFragment().ShouldBe(expectedUpdateFragment);
            var expectedPgType = pgType ?? "varchar";
            field.PgType.ShouldBe(expectedPgType);
            field.UpsertArgument.PostgresType.ShouldBe(expectedPgType);           
            field.DbType.ShouldBe(NpgsqlDbType.Text);
        }

        [Theory]
        [InlineData(null, "user_id = CAST(data ->> 'UserId' as uuid)")]
        [InlineData("uuid", "user_id = CAST(data ->> 'UserId' as uuid)")]
        [InlineData("text", "user_id = CAST(data ->> 'UserId' as text)")]
        public void pg_type_is_used_for_guid(string pgType, string expectedUpdateFragment)
        {
            var field = DuplicatedField.For<Target>(EnumStorage.AsInteger, x => x.UserId);
            field.PgType = pgType ?? field.PgType;

            field.UpdateSqlFragment().ShouldBe(expectedUpdateFragment);
            var expectedPgType = pgType ?? "uuid";
            field.PgType.ShouldBe(expectedPgType);
            field.UpsertArgument.PostgresType.ShouldBe(expectedPgType);
            field.DbType.ShouldBe(NpgsqlDbType.Uuid);
        }

        [Theory]
        [InlineData(null, "tags_array = CAST(ARRAY(SELECT jsonb_array_elements_text(CAST(data ->> 'TagsArray' as jsonb))) as varchar[])")]
        [InlineData("varchar[]", "tags_array = CAST(ARRAY(SELECT jsonb_array_elements_text(CAST(data ->> 'TagsArray' as jsonb))) as varchar[])")]
        [InlineData("text[]", "tags_array = CAST(ARRAY(SELECT jsonb_array_elements_text(CAST(data ->> 'TagsArray' as jsonb))) as text[])")]
        public void pg_type_is_used_for_string_array(string pgType, string expectedUpdateFragment)
        {
            var field = DuplicatedField.For<Target>(EnumStorage.AsInteger, x => x.TagsArray);
            field.PgType = pgType ?? field.PgType;

            field.UpdateSqlFragment().ShouldBe(expectedUpdateFragment);
            var expectedPgType = pgType ?? "varchar[]";
            field.PgType.ShouldBe(expectedPgType);
            field.UpsertArgument.PostgresType.ShouldBe(expectedPgType);
            field.DbType.ShouldBe(NpgsqlDbType.Array | NpgsqlDbType.Text);
        }

        [Theory]
        [InlineData(null, "tags_list = CAST(data ->> 'TagsList' as jsonb)")]
        [InlineData("varchar[]", "tags_list = CAST(ARRAY(SELECT jsonb_array_elements_text(CAST(data ->> 'TagsList' as jsonb))) as varchar[])")]
        [InlineData("text[]", "tags_list = CAST(ARRAY(SELECT jsonb_array_elements_text(CAST(data ->> 'TagsList' as jsonb))) as text[])")]
        public void pg_type_is_used_for_string_list(string pgType, string expectedUpdateFragment)
        {
            var field = DuplicatedField.For<ListTarget>(EnumStorage.AsInteger, x => x.TagsList);
            field.PgType = pgType ?? field.PgType;

            field.UpdateSqlFragment().ShouldBe(expectedUpdateFragment);
            var expectedPgType = pgType ?? "jsonb";
            field.PgType.ShouldBe(expectedPgType);
            field.UpsertArgument.PostgresType.ShouldBe(expectedPgType);
            field.DbType.ShouldBe(NpgsqlDbType.Array | NpgsqlDbType.Text);
        }

        private class ListTarget
        {
            public List<string> TagsList { get; set; }
        }

    }
}

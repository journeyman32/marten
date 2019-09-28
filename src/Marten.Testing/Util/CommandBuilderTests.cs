using Baseline.Reflection;
using Marten.Util;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Xunit;

namespace Marten.Testing.Util
{
    public class CommandBuilderTests
    {
        [Theory]
        [InlineData("data", Casing.Default, "data -> 'Inner' ->> 'AnotherString'")]
        [InlineData("d.data", Casing.CamelCase, "d.data -> 'inner' ->> 'anotherString'")]
        [InlineData("string", Casing.SnakeCase, "string -> 'inner' ->> 'another_string'")]
        public void build_json_string_locator(string column, Casing casing, string expected)
        {
            var members = new MemberInfo[] { ReflectionHelper.GetProperty<Target>(x => x.Inner), ReflectionHelper.GetProperty<Target>(x => x.AnotherString) };
            var locator = CommandBuilder.BuildJsonStringLocator(column, members, casing);
            locator.ShouldBe(expected);
        }

        [Theory]
        [InlineData("data", Casing.Default, "data -> 'Inner' -> 'AnotherString'")]
        [InlineData("d.data", Casing.CamelCase, "d.data -> 'inner' -> 'anotherString'")]
        [InlineData("string", Casing.SnakeCase, "string -> 'inner' -> 'another_string'")]
        public void build_json_object_locator(string column, Casing casing, string expected)
        {
            var members = new MemberInfo[] { ReflectionHelper.GetProperty<Target>(x => x.Inner), ReflectionHelper.GetProperty<Target>(x => x.AnotherString) };
            var locator = CommandBuilder.BuildJsonObjectLocator(column, members, casing);
            locator.ShouldBe(expected);
        }
    }
}

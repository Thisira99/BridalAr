using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.Properties.Tests
{
    [TestFixture]
    partial class PropertyVisitorTests
    {
        class Adapters
        {
            class TestAdapter : IVisitContravariantPropertyAdapter<IBase>
            {
                public void Visit<TContainer>(in VisitContext<TContainer> context, ref TContainer container, IBase value)
                {
                    Debug.Log($"Visit {value.GetType()}");
                    context.ContinueVisitationWithoutAdapters(ref container);
                }
            }
            
            class ContinueAdapter : IVisitPropertyAdapter<ConcreteA>, 
                IVisitContravariantPropertyAdapter<IBase>
            {
                public void Visit<TContainer>(in VisitContext<TContainer> context, ref TContainer container, IBase value)
                {
                    context.ContinueVisitationWithoutAdapters(ref container);
                }

                public void Visit<TContainer>(in VisitContext<TContainer, ConcreteA> context, ref TContainer container, ref ConcreteA value)
                {
                    context.ContinueVisitation(ref container, ref value);
                }
            }

            interface IBase
            {
            }

            class ConcreteA : IBase
            {
            }

            class ConcreteB : IBase
            {
            }

            class ConcreteC : IBase
            {
            }

            class Container
            {
                public IBase Base;
                public ConcreteA A;
                public ConcreteB B;
                public ConcreteC C;
                public int Ignored;

                internal class PropertyBag : ContainerPropertyBag<Container>
                {
                    public PropertyBag()
                    {
                        AddProperty(new DelegateProperty<Container, IBase>(
                                        name: nameof(Base),
                                        getter: (ref Container c) => c.Base,
                                        setter: (ref Container c, IBase v) => c.Base = v));

                        AddProperty(new DelegateProperty<Container, ConcreteA>(
                                        name: nameof(A),
                                        getter: (ref Container c) => c.A,
                                        setter: (ref Container c, ConcreteA v) => c.A = v));

                        AddProperty(new DelegateProperty<Container, ConcreteB>(
                                        name: nameof(B),
                                        getter: (ref Container c) => c.B,
                                        setter: (ref Container c, ConcreteB v) => c.B = v));

                        AddProperty(new DelegateProperty<Container, ConcreteC>(
                                        name: nameof(C),
                                        getter: (ref Container c) => c.C,
                                        setter: (ref Container c, ConcreteC v) => c.C = v));

                        AddProperty(new DelegateProperty<Container, int>(
                                        name: nameof(Ignored),
                                        getter: (ref Container c) => c.Ignored,
                                        setter: (ref Container c, int v) => c.Ignored = v));
                    }
                }
            }

            [SetUp]
            public void SetUp()
            {
                PropertyBag.Register(new Container.PropertyBag());
            }

            [Test]
            public void VisitAdapter_AdapterWithCovariance_VisitsAllDerivedTypes()
            {
                var container = new Container
                {
                    Base = new ConcreteA(),
                    A = new ConcreteA(),
                    B = new ConcreteB(),
                    C = new ConcreteC(),
                };

                PropertyContainer.Accept(new TestVisitor().WithAdapter<TestAdapter>(), ref container);
                LogAssert.Expect(LogType.Log, $"Visit Unity.Properties.Tests.PropertyVisitorTests+Adapters+{nameof(ConcreteA)}");
                LogAssert.Expect(LogType.Log, $"Visit Unity.Properties.Tests.PropertyVisitorTests+Adapters+{nameof(ConcreteB)}");
                LogAssert.Expect(LogType.Log, $"Visit Unity.Properties.Tests.PropertyVisitorTests+Adapters+{nameof(ConcreteC)}");
            }

            [Test]
            public void PropertyVisitor_WithVisitAdapter_DoesNotAllocateAdditionalMemory()
            {
                var container = new Container
                {
                    Base = new ConcreteA(),
                    A = new ConcreteA(),
                    B = new ConcreteB(),
                    C = new ConcreteC(),
                };
                var visitor = new TestVisitor().WithAdapter<ContinueAdapter>();
                
                GCAllocTest.Method(() =>
                    {
                        PropertyContainer.Accept(visitor, ref container);
                    })
                    .ExpectedCount(0)
                    .Warmup()
                    .Run();
            }
        }
    }
}
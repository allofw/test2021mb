using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

// royks: по код-стайлу (например, expression-body методы, использование var), замечаний не делаю, всё зависит от договорённостей + решается клинапом

namespace FiguresDotStore.Controllers
{
    internal interface IRedisClient
    {
        int Get(string type); // royks: малопонятные имена методов
        void Set(string type, int current);
    }

    public static class FiguresStorage // royks: статический класс просто как набор методов, не вижу причин делать статику
    {
        // корректно сконфигурированный и готовый к использованию клиент Редиса
        private static IRedisClient RedisClient { get; } // royks: инициализации не вижу, если только принимать ограничения тестовой задачи (комментарий выше)

        public static bool CheckIfAvailable(string type, int count) // royks: в данном случае метод не нужен, проверкку на доступность лучше делать при резервировании
        {
            return RedisClient.Get(type) >= count;
        }

        public static void Reserve(string type, int count) // royks: непонятно, почему тип строкой, имеет смысл написать полноценный сериализатов в енам
        {
            var current = RedisClient.Get(type); // royks: возможно не хватает транзакционности (по именам методом Get/Set мало понятно, что там происходит)

            RedisClient.Set(type, current - count);
        }
    }

    public class Position // royks: классы Position и Cart нужны только для маппинга в Order, в принципе, такое бывает иногда (разделение service-level и data-level), но не в данном примере
    {
        public string Type { get; set; }

        public float SideA { get; set; }
        public float SideB { get; set; }
        public float SideC { get; set; }

        public int Count { get; set; }
    }

    public class Cart
    {
        public List<Position> Positions { get; set; }
    }

    public class Order
    {
        public List<Figure> Positions { get; set; }

        public decimal GetTotal() => // royks: непонятно, для чего этот метод
            Positions.Select(p => p switch // royks: нет дефолтного значения
                {
                    Triangle => (decimal)p.GetArea() * 1.2m,
                    Circle => (decimal)p.GetArea() * 0.9m
                })
                .Sum();
    }

    public abstract class Figure // royks: с учётом замечания ниже тут вообще интерфейс напрашивается, а не абстрактный базовый класс
    {
        public float SideA { get; set; } // royks: не надо в базовый класс пихать все возможные свойства из потомков, немасштабируемо, лишние данные в куче мест, по имени не понятно, что хранится
        public float SideB { get; set; }
        public float SideC { get; set; }

        public abstract void Validate();
        public abstract double GetArea();
    }

    public class Triangle : Figure
    {
        public override void Validate()
        {
            bool CheckTriangleInequality(float a, float b, float c) => a < b + c;
            if (CheckTriangleInequality(SideA, SideB, SideC)
                && CheckTriangleInequality(SideB, SideA, SideC)
                && CheckTriangleInequality(SideC, SideB, SideA))
                return;
            throw new InvalidOperationException("Triangle restrictions not met");   // royks: куча похожих исключений для каждой фигуры, нужно сделать базовое, от него наследовать для каждой фигуры, либо параметром фигуру прокидывать
            // royks: не хватает проверок, например, чтоб стороны не были отрицательными
        }

        public override double GetArea()
        {
            var p = (SideA + SideB + SideC) / 2; // royks: скорее всего нет, но можем и выйти за рамки float
            return Math.Sqrt(p * (p - SideA) * (p - SideB) * (p - SideC));
        }

    }

    public class Square : Figure
    {
        public override void Validate()
        {
            if (SideA < 0)
                throw new InvalidOperationException("Square restrictions not met");

            if (SideA != SideB) // royks: проверка была бы не нужна, если стороны квадрата хранили в одно месте (SideA)
                throw new InvalidOperationException("Square restrictions not met");
            // royks: валидацая [возможно] неполная, нулевая площадь, например
            
            // royks: вообще, проверки во всех фигурах очень похожие, я бы вынес валидацию в базовый класс (или дефолнтуню реализацию интерфейса),
            // а переоперелял бы только условия проверки (ниже я сделал переменную в рамках этого метода)
            
            //var conditions = new (Func<bool> condition, bool invalid)[] // bool invalid - это для простоты, можно сюда закинуть подробности, почему валидация не прошла или вообще Action
            //{
            //    (() => SideA < 0, true),
            //    (() => SideA != SideB, true),
            //    (() => true, false)
            //};
            //
            //if (conditions.First(t => t.condition()).invalid)
            //    throw new InvalidOperationException("Square restrictions not met");
        }

        public override double GetArea() => SideA * SideA;
    }

    public class Circle : Figure
    {
        public override void Validate()
        {
            if (SideA < 0)
                throw new InvalidOperationException("Circle restrictions not met");
        }

        public override double GetArea() => Math.PI * SideA * SideA;
    }

    public interface IOrderStorage
    {
        // сохраняет оформленный заказ и возвращает сумму
        Task<decimal> Save(Order order); // royks: я бы ещё параметром передавал сюда транзакцию или объект unitOfWork (с транзакцией и всеми репозиториями)
    }

    [ApiController]
    [Route("[controller]")]
    public class FiguresController : ControllerBase
    {
        private readonly ILogger<FiguresController> _logger; // royks: скорее всего издержки тестового задания, но я нигде использования логгера не увидел
        private readonly IOrderStorage _orderStorage;

        public FiguresController(ILogger<FiguresController> logger, IOrderStorage orderStorage)
        {
            _logger = logger;
            _orderStorage = orderStorage;
        }

        // хотим оформить заказ и получить в ответе его стоимость
        [HttpPost]
        public async Task<ActionResult> Order(Cart cart)
        {
            foreach (var position in cart.Positions)
            {
                if (!FiguresStorage.CheckIfAvailable(position.Type, position.Count))
                {
                    return new BadRequestResult(); // royks: всё записит, конечно, от соглашений, но кидать BadRequest тут кажется не очень уместным
                }
            }

            var order = new Order
            {
                Positions = cart.Positions.Select(p =>
                {
                    Figure figure = p.Type switch // royks: нет дефолтного варианта
                    {
                        "Circle" => new Circle(),
                        "Triangle" => new Triangle(),
                        "Square" => new Square()
                    };
                    figure.SideA = p.SideA;
                    figure.SideB = p.SideB;
                    figure.SideC = p.SideC;
                    figure.Validate();
                    return figure; // royks: да и маппинг в целом лучше вынести в отдельную сущность
                }).ToList()
            };

            foreach (var position in cart.Positions)
            {
                FiguresStorage.Reserve(position.Type, position.Count); // royks: и получаем неконсистентные данные, транзакций нет, проверка отдельно от резервирования
            } // royks: резервируем по cart, а сохраняем по order, совсем запутились, выше писал, что класс Cart лишний

            var result = _orderStorage.Save(order); // royks: метод асинхронный, смело ставим await и убимраем .Result ниже

            return new OkObjectResult(result.Result);
        } // royks: в целом метод контроллера содержит кучу логики, которую имеет смысл вынести в какой-нибудь IOrderService, оставив его, обёрнутый в try-catch и маппинги из дтошек
    }
}
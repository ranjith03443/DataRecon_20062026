using Microsoft.AspNetCore.Mvc;

namespace DataReconciliation.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index() => RedirectToAction("Index", "Workflow");
        public IActionResult Error() => View();
    }
}

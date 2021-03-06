using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using HistoryContest.Server.Data;
using HistoryContest.Server.Services;
using HistoryContest.Server.Extensions;
using HistoryContest.Server.Models.ViewModels;
using HistoryContest.Server.Models.Entities;
using System.Threading;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace HistoryContest.Server.Controllers.APIs
{
    [Authorize]
    //[ValidateAntiForgeryToken]
    [Produces("application/json")]
    [Route("api/[controller]")]
    public class ResultController : Controller
    {
        private readonly UnitOfWork unitOfWork;
        private readonly QuestionSeedService questionSeedService;

        public ResultController(UnitOfWork unitOfWork)
        {
            this.unitOfWork = unitOfWork;
            questionSeedService = new QuestionSeedService(unitOfWork);
        }

        /// <summary>
        /// 获取一位学生的考试结果
        /// </summary>
        /// <remarks>
        /// 根据学生的学号(ID)返回该学生的考试结果。
        ///    
        /// ID参数是可选的。如果不输入ID，且当前用户认证为学生，则取Session中的学号作为ID。
        ///    
        /// 使用情景：
        /// 1. 学生考试完毕后重新登录时，将页面重定向到调用这个api；
        /// 2. 辅导员在看本院得分情况时，想要查看某位学生的考试详细细节。
        /// </remarks>
        /// <param name="id?">学生的学号（可选）</param>
        /// <returns>学号对应的考试结果</returns>
        /// <response code="200">
        /// 返回欲查询的学生的考试结果，由以下几部分组成：
        /// * 分数
        /// * 完成时间、考试用时
        /// * 答题细节
        ///     - 答题细节为被查询的学生所做的30道题的情况构成的数组，
        ///       每个元素由问题ID、正确答案、学生提交的答案构成。
        /// </response>
        /// <response code="400">当前用户不是学生或对应Session中没有ID</response>
        /// <response code="403">被查询的学生没有完成考试</response>
        /// <response code="404">传入ID没有对应的学生</response>
        [HttpGet("{id?}")]
        [ProducesResponseType(typeof(ResultViewModel), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetResult(string id)
        {
            if (id == null)
            { // 如果不输入id，且当前用户认证为学生，则取Session中的学号作为id
                if (this.Session().CheckRole("Student") && this.Session().ID != null)
                {
                    id = this.Session().ID;
                    if (id == null)
                    {
                        return BadRequest("Student ID not set in the session, please login again");
                    }
                }
                else
                {
                    return BadRequest("Empty argument request invalid");
                }
            }

            if (!id.IsStudentID())
            {
                return BadRequest("Argument is not a student ID");
            }

            ResultViewModel model = await unitOfWork.Cache.Results().GetAsync(id);
            if (model == null)
            {
                var student = await unitOfWork.Cache.StudentEntities(id.ToDepartment()).GetAsync(id);
                if (student == null)
                {
                    return NotFound("Student not found");
                }
                if (!student.IsTested)
                {
                    return Forbid();
                }

                model = new ResultViewModel { Score = student.Score ?? 0, TimeConsumed = student.TimeConsumed, TimeFinished = student.DateTimeFinished };
                if (student.IsTested)
                {
                    var seed = (await unitOfWork.Cache.QuestionSeeds().GetAsync((int)student.QuestionSeedID));
                    model.Details = seed.QuestionIDs.Zip(student.Choices, (ID, choice) => new ResultDetailViewModel
                    {
                        ID = ID,
                        Correct = unitOfWork.Cache.Answers()[ID].Answer,
                        Submit = choice
                    }).ToList();
                }
                await unitOfWork.Cache.Results().SetAsync(student.ID.ToStringID(), model);
            }

            return Json(model);
        }



        /// <summary>
        /// 计算学生考试分数
        /// </summary>
        /// <remarks>
        /// **注意**：目前后端的实现暂时仍在采用*遍历前端传过来的answers数组*来计算分数（也就是说学生没选答案的题目如果没有传给后端，会造成遗漏）
        /// 
        /// 分数计算完成后，会在后端更新各种信息，然后注销账户。
        /// </remarks>
        /// <param name="submittedAnswers">问题ID与考生选择所构数组</param>
        /// <returns>考生的考试结果</returns>
        /// <response code="200">返回考生的考试结果。该结果JSON的模型与`GET api/Result/{id}`相同，可以在将来重新查询到</response>
        /// <response code="400">
        /// * 传进的数组格式不合法
        /// * 答案数组里有一个ID没有对应的问题
        /// </response>
        /// <response code="403">考生已考完</response>
        [HttpPost]
        [Authorize(Roles = "Student, Administrator")]
        [ProducesResponseType(typeof(ResultViewModel), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> CountScore([FromBody]List<SubmittedAnswerViewModel> submittedAnswers)
        {
            var timeElapsed = DateTime.Now - this.Session().TestBeginTime;
            if (timeElapsed >= unitOfWork.Configuration.TestTime + TimeSpan.FromMinutes(3) || timeElapsed <= TimeSpan.FromMinutes(3))
            {
                return BadRequest("Test time not in the proper range!");
            }

            var size = unitOfWork.Configuration.QuestionCount.Choice + unitOfWork.Configuration.QuestionCount.TrueFalse;
            if(!ModelState.IsValid || submittedAnswers.Count != size)
            {
                return BadRequest("Body JSON content invalid or does not fit the size: " + size);
            }

            if (this.Session().TestState == TestState.Tested)
            {
                return Forbid();
            }

            string studentID = this.Session().ID;
            if (studentID == null)
            {
                return BadRequest("Student ID not set in the session, please login again");
            }

            if (await unitOfWork.Cache.Database.StringGetAsync("CountingScore:" + studentID) == "true")
            {
                return BadRequest("Score has already been counting");
            }
            await unitOfWork.Cache.Database.StringSetAsync("CountingScore:" + studentID, "true");

            try
            {
                var seed = this.Session().SeedID;
                if (seed == null)
                {
                    await unitOfWork.Cache.Database.KeyDeleteAsync("CountingScore:" + studentID);
                    return BadRequest("Question seed not created");
                }

                #region calculate score and update student data
                var studentDictionary = unitOfWork.Cache.StudentEntities(studentID.ToDepartment());
                var student = await studentDictionary.GetAsync(studentID);
                student.Score = 0;
                student.QuestionSeedID = (int)seed;
                student.DateTimeFinished = DateTime.Now;
                student.Choices = submittedAnswers.Select(a => (byte)a.Answer).ToArray();

                if (this.Session().TestBeginTime == null)
                {
                    await unitOfWork.Cache.Database.KeyDeleteAsync("CountingScore:" + studentID);
                    return BadRequest("Test begin time not set in the session");
                }
                student.TimeConsumed = student.DateTimeFinished - this.Session().TestBeginTime;

                var correctAnswers = await questionSeedService.GetAnswersBySeedID((int)seed);

                var model = new ResultViewModel
                {
                    Details = new List<ResultDetailViewModel>(capacity: size),
                    TimeFinished = (DateTime)student.DateTimeFinished,
                    TimeConsumed = (TimeSpan)student.TimeConsumed
                };
                for (int i = 0; i < submittedAnswers.Count; ++i)
                {
                    var submit = submittedAnswers[i];
                    var correct = correctAnswers[i];
                    if (submit.ID != correct.ID)
                    {
                        await unitOfWork.Cache.Database.KeyDeleteAsync("CountingScore:" + studentID);
                        return BadRequest("Encounter improper ID in your answer set: " + submit.ID + " at " + i);
                    }
                    student.Score += submit.Answer == correct.Answer ? correct.Points : 0;
                    model.Details.Add(new ResultDetailViewModel { ID = correct.ID, Correct = correct.Answer, Submit = submit.Answer });
                }
                model.Score = (int)student.Score;
                #endregion

                #region save data
                var summary = await ScoreSummaryByDepartmentViewModel.GetAsync(unitOfWork, student.Counselor);
                await summary.UpdateAsync(unitOfWork, student); // 更新院系概况，放在前面防止重复计算
                await unitOfWork.Cache.StudentEntities(student.Department).SetAsync(studentID, student); // 更新Student
                await unitOfWork.Cache.StudentViewModels(student.Department).SetAsync(studentID, (StudentViewModel)student);
                await unitOfWork.Cache.Database.ListRightPushAsync("StudentIDsToUpdate", studentID); // 学生ID放入待更新列表
                await unitOfWork.Cache.Results().SetAsync(studentID, model); // result存入缓存
                new ExcelExportService(unitOfWork).UpdateExcelByStudent(student);
                #endregion
                this.Session().TestState = TestState.Tested;

                #region logout
                HttpContext.Session.Clear(); // 注销账户
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                await unitOfWork.Cache.Database.KeyDeleteAsync("CountingScore:" + studentID); // 表示没有正在计算
                #endregion
                return Json(model);
            }
            catch(Exception ex)
            {
                this.Session().TestState = TestState.Testing;
                await unitOfWork.Cache.Database.KeyDeleteAsync("CountingScore:" + studentID);
                Console.WriteLine(ex.ToString());
                return BadRequest(ex.ToString());
            }
        }

        ///// <summary>
        ///// 获取一套试卷的所有答案
        ///// </summary>
        ///// <remarks>
        ///// 这个API将问题种子对应的所有问题的答案及分值返回，让前端在本地计算分数。可能在分担服务器计算负担上有所帮助。  
        ///// </remarks>
        ///// <returns>当前问题种子对应的所有问题的答案</returns>
        ///// <response code="200">返回当前用户Session中存储的种子中的所有问题的答案、分值</response>
        ///// <response code="400">当前用户没有对应的问题种子</response>
        //[HttpPost("Answer")]
        //[Authorize(Roles = "Student, Administrator")]
        //[ProducesResponseType(typeof(List<CorrectAnswerViewModel>), StatusCodes.Status200OK)]
        //[ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        //public async Task<IActionResult> GetAllAnswers()
        //{
        //    var seed = this.Session().SeedID;
        //    if (seed == null)
        //    {
        //        return BadRequest("Question seed not created");
        //    }
        //
        //    var answers = await questionSeedService.GetAnswersBySeedID((int)seed);
        //    if (answers == null)
        //    {
        //        throw new Exception("Improper seed created, ID: " + seed);
        //    }
        //
        //    return Json(answers);
        //}

        ///// <summary>
        ///// 获取一道题的答案
        ///// </summary>
        ///// <remarks>
        ///// 这个API主要是配合 `POST api/question` 使用，使前端能够通过题号分批分次地检索答案，在本地计算分数。
        ///// </remarks>
        ///// /// <param name="id">问题对应的唯一ID</param>
        ///// <returns>ID对应问题的答案</returns>
        ///// <response code="200">返回ID对应问题的答案、分值</response>
        ///// <response code="404">ID没有对应的问题</response>
        //[HttpGet("Answer/{id}")]
        //[Authorize(Roles = "Student, Administrator")]
        //[ProducesResponseType(typeof(CorrectAnswerViewModel), StatusCodes.Status200OK)]
        //[ProducesResponseType(StatusCodes.Status404NotFound)]
        //public async Task<IActionResult> GetAnswerByID(int id)
        //{
        //    var answer = await unitOfWork.QuestionRepository.GetAnswerFromCacheAsync(id);
        //    if (answer == null)
        //    {
        //        return NotFound();
        //    }

        //    return Json(answer);
        //}
    }
}

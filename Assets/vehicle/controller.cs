using UnityEngine;
using System.Collections;

[RequireComponent(typeof(wheelsManager)) ]
[RequireComponent(typeof(engineAudio)) ]
[RequireComponent(typeof(inputManager)) ]
public class controller : MonoBehaviour{
    
    internal enum driveType{
        frontWheelDrive,
        rearWheelDrive,
        allWheelDrive
    }
    [SerializeField]private driveType drive;

    //scripts ->
    private engineAudio audio;
    private inputManager IM;
    private wheelsManager wheelsmanager;

    //components
	private WheelFrictionCurve  forwardFriction,sidewaysFriction;
    private new Rigidbody rigidbody;
    private WheelCollider[] wheels ;
    private GameObject centerOfMass;
    private Animator charAnim;
    [Header("Power Curve")]
    public AnimationCurve enginePower;


    [Header("Misc")]
    public Material brakeLights;
    public GameObject steeringWheel;

    [Header("Variables")]
    public bool isAutomatic;
    public float maxRPM , minRPM;
    [Range(1.5f,4)]public float finalDrive ;
    public float[] gears;
    [Range(5,20)]public float DownForceValue ;
    [Range(0.01f,0.02f)]public float dragAmount ;
    [Range (0,1)] public float EngineSmoothTime = 0.2f ;


    [HideInInspector]public float ForwardStifness;
    [HideInInspector]public float SidewaysStifness;
    [HideInInspector]public float KPH;

    private int gearNum = 1;
    private float engineRPM;
    private float totalPower;
    private float[] wheelSlip;
    private float finalTurnAngle;
    private float radius  = 4;
    private float wheelsRPM  ;
    private float horizontal ;
    private float acceleration;
    private float vertical ;
    private float downforce ;
    private float gearChangeRate;
    private float brakPower;
    private float engineLerpValue;
    private float engineLoad = 1;
    private float animatorTurnAngle;

    private bool reverse = false;
    private bool lightsFlag ; 
    private bool grounded ;
    private bool engineLerp ;

    private void Start() {
        getObjects();
    }

    private void Update() {

        addDownForce();
        steerVehicle();
        calculateEnginePower();
        friction();
        Audio();
        if(isAutomatic)
            shifter();
        else
            manual();


    }

    void FixedUpdate(){
        if(brakeLights != null){
        activateLights();    
        }
        animatorTurnAngle = Mathf.Lerp(animatorTurnAngle , -horizontal , 20 * Time.deltaTime);

        //steeringWheel.transform.Rotate(transform.up * animatorTurnAngle );
        if(steeringWheel != null){
            charAnim.SetFloat("turnAngle" , animatorTurnAngle);
            steeringWheel.transform.localRotation =Quaternion.Euler(0,animatorTurnAngle * 35 , 0);
        }
    }

    void Audio(){
        audio.totalPower = totalPower;
        audio.engineRPM = engineRPM;
        audio.engineLerp = engineLerp;
    }

    private void activateLights() {
        if (IM.vertical < 0 || KPH <= 1) turnLightsOn();
        else turnLightsOff();
    }
   
    private void turnLightsOn(){
        if (lightsFlag) return;
        brakeLights.SetColor("_EmissionColor", new Color(255f,35f,35f) * 0.115f);
        lightsFlag = true;
    }    
   
    private void turnLightsOff(){
        if (!lightsFlag) return;
        brakeLights.SetColor("_EmissionColor", Color.black);
        lightsFlag = false;
    }

    private void calculateEnginePower(){
        lerpEngine();
        wheelRPM();

        acceleration = vertical > 0 ?  vertical : wheelsRPM <= 1 ? vertical  : 0 ;
        
        if(!isGrounded()){
            acceleration = engineRPM > 1000 ? acceleration / 2 : acceleration; 
        }


        if(engineRPM >= maxRPM){
            setEngineLerp(maxRPM - 1000);
        }
        if(!engineLerp){
            engineRPM = Mathf.Lerp(engineRPM,1000f + Mathf.Abs(wheelsRPM) *  finalDrive *  (gears[gearNum]) , (EngineSmoothTime * 10) * Time.deltaTime);
            totalPower = enginePower.Evaluate(engineRPM) * (gears[gearNum] * finalDrive ) * acceleration  ;
        }
        
        
        engineLoad = Mathf.Lerp(engineLoad,vertical - ((engineRPM - 1000) / maxRPM ),(EngineSmoothTime * 10) * Time.deltaTime);

        moveVehicle();
    }

    private void wheelRPM(){
        float sum = 0;
        int R = 0;
        for (int i = 0; i < 4; i++)
        {
            sum += wheels[i].rpm;
            R++;
        }
        wheelsRPM = (R != 0) ? sum / R : 0;
 
        if(wheelsRPM < 0 && !reverse ){
            reverse = true;
            //if (gameObject.tag != "AI") manager.changeGear();
        }
        else if(wheelsRPM > 0 && reverse){
            reverse = false;
            //if (gameObject.tag != "AI") manager.changeGear();
        }
    }

    public void manual(){

        if((Input.GetAxis("Fire2") == 1  ) && gearNum <= gears.Length && Time.time >= gearChangeRate ){
            gearNum  = gearNum +1;
            gearChangeRate = Time.time + 1f/3f ;
            setEngineLerp(engineRPM - ( engineRPM > 1500 ? 2000 : 700));
            audio.DownShift();

        }
        if((Input.GetAxis("Fire3") == 1 ) && gearNum >= 1  && Time.time >= gearChangeRate){
            gearChangeRate = Time.time + 1f/3f ;
            gearNum --;
            setEngineLerp(engineRPM - ( engineRPM > 1500 ? 1500 : 700));
            audio.DownShift();
        }
    
    }

    private void shifter(){
        if(!isGrounded())return;

        if(engineRPM > maxRPM  && gearNum < gears.Length-1 && !reverse && Time.time >= gearChangeRate  && KPH >55){
            gearNum ++;
            audio.DownShift();
            setEngineLerp(engineRPM - (engineRPM / 3));
            gearChangeRate = Time.time + 1f/1f ;
        }
        if(engineRPM < minRPM && gearNum > 0 && Time.time >= gearChangeRate){
            gearChangeRate = Time.time + 0.15f ;
            setEngineLerp(engineRPM + (engineRPM / 2));
            gearNum --;
        }

    }
 
    public bool isGrounded(){
        if(wheels[0].isGrounded &&wheels[1].isGrounded &&wheels[2].isGrounded &&wheels[3].isGrounded )
            return true;
        else
            return false;
    }

    private void moveVehicle(){
        if(drive == driveType.rearWheelDrive){
            for (int i = 2; i < wheels.Length; i++){
                wheels[i].motorTorque = (vertical == 0) ? 0 : totalPower / (wheels.Length - 2) ;
            }
        }
        else if(drive == driveType.frontWheelDrive){
            for (int i = 0; i < wheels.Length - 2; i++){
                wheels[i].motorTorque =  (vertical == 0) ? 0 : totalPower / (wheels.Length - 2) ;
            }
        }
        else{
            for (int i = 0; i < wheels.Length; i++){
                wheels[i].motorTorque =  (vertical == 0) ? 0 : totalPower / wheels.Length;
            }
        }


        for (int i = 0; i < wheels.Length; i++){
            if(KPH <= 1 && KPH >= -1 && vertical == 0){
                brakPower = 5;
            } else{
                if(vertical < 0 && KPH > 1 && !reverse)
                    brakPower =  (wheelSlip[i] <= 0.3f) ? brakPower + -vertical * 100 : brakPower > 0 ? brakPower  + vertical * 50 : 0 ;
                else 
                    brakPower = 0;
            }
            wheels[i].brakeTorque = brakPower;
        }

        wheels[2].brakeTorque = wheels[3].brakeTorque = (IM.handbrake)? Mathf.Infinity : 0f;

        rigidbody.angularDamping = (KPH > 100)? KPH / 100 : 0;
        rigidbody.linearDamping = dragAmount + (KPH / 40000) ;

        KPH = rigidbody.linearVelocity.magnitude * 3.6f;

    }

    private void steerVehicle(){

        vertical = IM.vertical;
        horizontal = Mathf.Lerp(horizontal , IM.horizontal , (IM.horizontal != 0) ? 2 * Time.deltaTime : 3 * 2 * Time.deltaTime);

        finalTurnAngle = (radius > 5 ) ? radius : 5  ;

        if (horizontal > 0 ) {
				//rear tracks size is set to 1.5f       wheel base has been set to 2.55f
            wheels[0].steerAngle = Mathf.Rad2Deg * Mathf.Atan(2.55f / (finalTurnAngle - (1.5f / 2))) * horizontal;
            wheels[1].steerAngle = Mathf.Rad2Deg * Mathf.Atan(2.55f / (finalTurnAngle + (1.5f / 2))) * horizontal;
        } else if (horizontal < 0 ) {                                                          
            wheels[0].steerAngle = Mathf.Rad2Deg * Mathf.Atan(2.55f / (finalTurnAngle + (1.5f / 2))) * horizontal;
            wheels[1].steerAngle = Mathf.Rad2Deg * Mathf.Atan(2.55f / (finalTurnAngle - (1.5f / 2))) * horizontal;
			//transform.Rotate(Vector3.up * steerHelping);

        } else {
            wheels[0].steerAngle =0;
            wheels[1].steerAngle =0;
        }

    }
   
    private void getObjects(){
        IM = GetComponent<inputManager>();
        rigidbody = GetComponent<Rigidbody>();
        audio = GetComponent<engineAudio>();
        wheelsmanager = GetComponent<wheelsManager>();
        if(steeringWheel != null)
        charAnim = GameObject.FindGameObjectWithTag("char").GetComponent<Animator>();
        wheels = wheelsmanager.wheels;
        wheelSlip = new float[wheels.Length];
        rigidbody.centerOfMass = gameObject.transform.Find("centerOfMas").gameObject.transform.localPosition;   

        audio.maxRPM = maxRPM;

    }

    private void addDownForce(){
        downforce = Mathf.Abs( DownForceValue * rigidbody.linearVelocity.magnitude);
        downforce = KPH > 60 ? downforce : 0;
        rigidbody.AddForce(-transform.up * downforce );

    }
  
    private void friction(){
    
        WheelHit hit;
        float sum = 0;
        float[] sidewaysSlip = new float[wheels.Length];
        for (int i = 0; i < wheels.Length ; i++){
            if(wheels[i].GetGroundHit(out hit) && i >= 2 ){
                forwardFriction = wheels[i].forwardFriction;
                forwardFriction.stiffness = (IM.handbrake)?  .55f : ForwardStifness; 
                wheels[i].forwardFriction = forwardFriction;

                sidewaysFriction = wheels[i].sidewaysFriction;
                sidewaysFriction.stiffness = (IM.handbrake)? .55f : SidewaysStifness;
                wheels[i].sidewaysFriction = sidewaysFriction;
                
                grounded = true;

                sum += Mathf.Abs(hit.sidewaysSlip);

            }
            else grounded = false;

            wheelSlip[i] = Mathf.Abs( hit.forwardSlip ) + Mathf.Abs(hit.sidewaysSlip) ;
            sidewaysSlip[i] = Mathf.Abs(hit.sidewaysSlip);


        }

        sum /= wheels.Length - 2 ;
        radius = (KPH > 60) ?  4 + (sum * -25) + KPH / 8 : 4;
        
    }
   
    private void setEngineLerp(float num){
        engineLerp = true;
        engineLerpValue = num;
    }

    public void lerpEngine(){
        if(engineLerp){
            totalPower = 0;
            engineRPM = Mathf.Lerp(engineRPM,engineLerpValue,20 * Time.deltaTime );
            engineLerp = engineRPM <= engineLerpValue + 100 ? false : true;
        }
    }   

    private string s ;
 
    void OnGUI(){
        s = "";
        foreach (float item in wheelSlip){
            s +=  item.ToString("0.0") + " ";
        }
        float pos = 50;

        GUI.Label(new Rect(20, pos, 200, 20),"currentGear: " + gearNum.ToString("0"));
        pos+=25f;
        GUI.HorizontalSlider(new Rect(20, pos, 200, 20), engineRPM,0,maxRPM);
        pos+=25f;
        GUI.Label(new Rect(20, pos, 200, 20),"wheel slip: " + s);
        pos+=25f;
        GUI.Label(new Rect(20, pos, 200, 20),"Torque: " + totalPower.ToString("0"));
        pos+=25f;
        GUI.Label(new Rect(20, pos, 200, 20),"KPH: " + KPH.ToString("0"));
        pos+=25f;
        GUI.HorizontalSlider(new Rect(20, pos, 200, 20), engineLoad, 0.0F, 1.0F);
        pos+=25f;
        GUI.Label(new Rect(20, pos, 200, 20),"brakes: " + brakPower.ToString("0"));
        pos+=25f;

        

        
    }

}